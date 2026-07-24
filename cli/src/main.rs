mod app_server;
mod editor;
mod renderer;
mod state;

use std::{
    env,
    path::{Path, PathBuf},
};

use anyhow::{Context, Result, bail};
use app_server::{AppServer, ServerEvent};
use clap::Parser;
use crossterm::event::{Event, EventStream};
use futures_util::StreamExt;
use renderer::{BlockKind, Renderer, TerminalSession};
use serde_json::{Value, json};
use state::{Action, AppState, ModelInfo};

#[derive(Parser)]
#[command(
    name = "devez",
    version,
    about = "Stable terminal UI for the official Codex app-server"
)]
struct Cli {
    /// Resume an existing Codex thread.
    #[arg(long, value_name = "THREAD_ID")]
    resume: Option<String>,

    /// Select a model from the app-server model catalog.
    #[arg(long)]
    model: Option<String>,

    /// Select a supported reasoning effort (for example high, xhigh, max).
    #[arg(long)]
    effort: Option<String>,

    /// Working directory. New threads default to the current directory.
    #[arg(long)]
    cwd: Option<PathBuf>,

    /// Codex executable used to launch `codex app-server`.
    #[arg(long, default_value = "codex")]
    codex: PathBuf,
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    let mut server = AppServer::spawn(&cli.codex).await?;

    let result = run(&cli, &mut server).await;
    server.shutdown().await;
    result
}

async fn run(cli: &Cli, server: &mut AppServer) -> Result<()> {
    server.initialize().await?;
    ensure_account(server).await?;

    let models_response = server
        .request("model/list", json!({ "includeHidden": true, "limit": 100 }))
        .await?;
    let models = parse_models(&models_response);
    if models.is_empty() {
        bail!("app-server가 사용 가능한 모델을 반환하지 않았습니다.");
    }

    let requested_model_name = choose_model(&models, cli.model.as_deref())?.model.clone();
    let cwd = resolve_cwd(cli.cwd.as_deref())?;
    let model_override = if cli.resume.is_some() {
        cli.model.as_deref()
    } else {
        Some(
            cli.model
                .as_deref()
                .unwrap_or(requested_model_name.as_str()),
        )
    };
    let thread_response = start_or_resume_thread(
        server,
        cli.resume.as_deref(),
        model_override,
        cli.cwd.as_ref().map(|_| cwd.as_path()),
        &cwd,
    )
    .await?;

    let thread = thread_response
        .get("thread")
        .context("thread 응답에 thread가 없습니다.")?;
    let thread_id = thread
        .get("id")
        .and_then(Value::as_str)
        .context("thread 응답에 id가 없습니다.")?
        .to_owned();
    let actual_model = thread_response
        .get("model")
        .and_then(Value::as_str)
        .unwrap_or(&requested_model_name)
        .to_owned();
    let actual_effort = cli.effort.clone().or_else(|| {
        thread_response
            .get("reasoningEffort")
            .and_then(Value::as_str)
            .map(ToOwned::to_owned)
    });
    validate_effort(&models, &actual_model, actual_effort.as_deref())?;
    let actual_cwd = thread_response
        .get("cwd")
        .and_then(Value::as_str)
        .unwrap_or_else(|| cwd.to_str().unwrap_or("."))
        .to_owned();

    let mut state = AppState::new(
        thread_id,
        actual_cwd,
        models,
        &actual_model,
        actual_effort.as_deref(),
    );
    if cli.resume.is_some() {
        state.load_history(thread);
    }

    let terminal = TerminalSession::enter()?;
    let mut renderer = Renderer::new();
    let ui_result = event_loop(server, &mut state, &mut renderer).await;
    let _ = renderer.finish();
    drop(terminal);
    ui_result
}

async fn event_loop(
    server: &mut AppServer,
    state: &mut AppState,
    renderer: &mut Renderer,
) -> Result<()> {
    let mut terminal_events = EventStream::new();
    draw(state, renderer)?;

    loop {
        let mut connection_closed = false;
        let action = tokio::select! {
            terminal_event = terminal_events.next() => {
                match terminal_event {
                    Some(Ok(Event::Key(key))) => state.handle_key(key),
                    Some(Ok(Event::Paste(text))) => {
                        state.handle_paste(&text);
                        Action::None
                    }
                    Some(Ok(Event::Resize(_, _))) => Action::None,
                    Some(Ok(_)) => Action::None,
                    Some(Err(error)) => {
                        state.push_notice(BlockKind::Error, "터미널 입력 오류", error.to_string());
                        Action::Quit
                    }
                    None => Action::Quit,
                }
            }
            server_event = server.next_event() => {
                match server_event {
                    Some(ServerEvent::Notification { method, params }) => {
                        state.handle_notification(&method, &params);
                        Action::None
                    }
                    Some(ServerEvent::Request { id, method, params }) => {
                        state.begin_server_request(id, &method, &params)
                    }
                    Some(ServerEvent::ProtocolWarning(message)) => {
                        state.push_notice(BlockKind::Warning, "프로토콜 경고", message);
                        Action::None
                    }
                    Some(ServerEvent::Closed(message)) => {
                        state.push_notice(BlockKind::Error, "연결 종료", message);
                        connection_closed = true;
                        Action::None
                    }
                    None => {
                        connection_closed = true;
                        Action::None
                    }
                }
            }
        };

        let should_quit = execute_action(server, state, renderer, action).await?;
        draw(state, renderer)?;
        if should_quit || connection_closed {
            break;
        }
    }
    Ok(())
}

async fn execute_action(
    server: &AppServer,
    state: &mut AppState,
    renderer: &mut Renderer,
    action: Action,
) -> Result<bool> {
    match action {
        Action::None => {}
        Action::Submit(text) => {
            let params = json!({
                "threadId": state.thread_id,
                "input": [{
                    "type": "text",
                    "text": text,
                    "text_elements": []
                }],
                "model": state.selected_model_name(),
                "effort": state.selected_effort()
            });
            match server.request("turn/start", params).await {
                Ok(response) => {
                    if let Some(turn_id) = response
                        .get("turn")
                        .and_then(|turn| turn.get("id"))
                        .and_then(Value::as_str)
                    {
                        state.set_turn_started(turn_id.to_owned());
                    }
                }
                Err(error) => state.set_request_failed(error.to_string()),
            }
        }
        Action::Steer(text) => {
            let Some(turn_id) = state.turn_id.clone() else {
                state.set_request_failed("활성 turn ID가 없어 추가 입력을 보낼 수 없습니다.");
                return Ok(false);
            };
            let params = json!({
                "threadId": state.thread_id,
                "expectedTurnId": turn_id,
                "input": [{
                    "type": "text",
                    "text": text,
                    "text_elements": []
                }]
            });
            if let Err(error) = server.request("turn/steer", params).await {
                state.push_notice(BlockKind::Error, "추가 입력 실패", error.to_string());
            }
        }
        Action::Interrupt => {
            if let Some(turn_id) = state.turn_id.clone() {
                let params = json!({
                    "threadId": state.thread_id,
                    "turnId": turn_id
                });
                if let Err(error) = server.request("turn/interrupt", params).await {
                    state.push_notice(BlockKind::Error, "중단 실패", error.to_string());
                }
            }
        }
        Action::NewThread => {
            let response = server
                .request(
                    "thread/start",
                    json!({
                        "cwd": state.cwd,
                        "model": state.selected_model_name(),
                        "sessionStartSource": "clear",
                        "threadSource": "devez-code-cli"
                    }),
                )
                .await;
            match response {
                Ok(response) => {
                    let thread_id = response
                        .get("thread")
                        .and_then(|thread| thread.get("id"))
                        .and_then(Value::as_str)
                        .map(ToOwned::to_owned);
                    let cwd = response
                        .get("cwd")
                        .and_then(Value::as_str)
                        .map(ToOwned::to_owned);
                    let model = response
                        .get("model")
                        .and_then(Value::as_str)
                        .map(ToOwned::to_owned);
                    let effort = response
                        .get("reasoningEffort")
                        .and_then(Value::as_str)
                        .map(ToOwned::to_owned);
                    if let (Some(thread_id), Some(cwd), Some(model)) = (thread_id, cwd, model) {
                        state.set_thread(thread_id, cwd, &model, effort.as_deref());
                        state.push_notice(
                            BlockKind::Success,
                            "새 대화",
                            "새 thread를 시작했습니다.",
                        );
                    } else {
                        state.set_request_failed("thread/start 응답이 올바르지 않습니다.");
                    }
                }
                Err(error) => state.set_request_failed(error.to_string()),
            }
        }
        Action::RpcResponse { id, result } => {
            if let Err(error) = server.respond(id, result) {
                state.push_notice(BlockKind::Error, "응답 전송 실패", error.to_string());
            }
        }
        Action::RpcError { id, message } => {
            if let Err(error) = server.respond_error(id, -32601, &message) {
                state.push_notice(BlockKind::Error, "오류 응답 실패", error.to_string());
            }
        }
        Action::ClearScreen => renderer.clear_screen()?,
        Action::Quit => return Ok(true),
    }
    Ok(false)
}

fn draw(state: &mut AppState, renderer: &mut Renderer) -> Result<()> {
    let committed = state.drain_committed();
    let view = state.view();
    renderer.render(&committed, view)
}

async fn ensure_account(server: &AppServer) -> Result<()> {
    let response = server
        .request("account/read", json!({ "refreshToken": false }))
        .await?;
    let requires_auth = response
        .get("requiresOpenaiAuth")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if requires_auth && response.get("account").is_none_or(Value::is_null) {
        bail!("OpenAI 로그인이 필요합니다. 공식 `codex login`을 먼저 실행하세요.");
    }
    Ok(())
}

async fn start_or_resume_thread(
    server: &AppServer,
    resume: Option<&str>,
    model: Option<&str>,
    resume_cwd: Option<&Path>,
    new_cwd: &Path,
) -> Result<Value> {
    if let Some(thread_id) = resume {
        let mut params = json!({ "threadId": thread_id });
        if let Some(model) = model {
            params["model"] = json!(model);
        }
        if let Some(cwd) = resume_cwd {
            params["cwd"] = json!(cwd.to_string_lossy());
        }
        server.request("thread/resume", params).await
    } else {
        server
            .request(
                "thread/start",
                json!({
                    "cwd": new_cwd.to_string_lossy(),
                    "model": model,
                    "sessionStartSource": "startup",
                    "threadSource": "devez-code-cli"
                }),
            )
            .await
    }
}

fn parse_models(response: &Value) -> Vec<ModelInfo> {
    response
        .get("data")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(ModelInfo::from_value)
        .collect()
}

fn choose_model<'a>(models: &'a [ModelInfo], requested: Option<&str>) -> Result<&'a ModelInfo> {
    if let Some(requested) = requested {
        return models
            .iter()
            .find(|model| {
                model.id == requested
                    || model.model == requested
                    || model.display_name.eq_ignore_ascii_case(requested)
            })
            .with_context(|| format!("모델 카탈로그에 `{requested}`가 없습니다."));
    }
    models
        .iter()
        .find(|model| model.is_default)
        .or_else(|| models.first())
        .context("기본 모델을 찾을 수 없습니다.")
}

fn validate_effort(models: &[ModelInfo], model_name: &str, effort: Option<&str>) -> Result<()> {
    let Some(effort) = effort else {
        return Ok(());
    };
    let Some(model) = models
        .iter()
        .find(|model| model.id == model_name || model.model == model_name)
    else {
        return Ok(());
    };
    if !model.supports_effort(effort) {
        let supported = model
            .efforts
            .iter()
            .map(|effort| effort.id.as_str())
            .collect::<Vec<_>>()
            .join(", ");
        bail!(
            "`{}` 모델은 `{effort}` reasoning을 지원하지 않습니다. 지원값: {supported}",
            model.display_name
        );
    }
    Ok(())
}

fn resolve_cwd(requested: Option<&Path>) -> Result<PathBuf> {
    let path = requested
        .map(Path::to_path_buf)
        .unwrap_or(env::current_dir().context("현재 작업 폴더를 확인할 수 없습니다.")?);
    path.canonicalize()
        .with_context(|| format!("작업 폴더를 열 수 없습니다: {}", path.display()))
}
