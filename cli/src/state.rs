use std::collections::{BTreeMap, HashMap};

use crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};
use serde_json::{Map, Value, json};

use crate::{
    editor::Editor,
    renderer::{Block, BlockKind, OverlayLine, OverlayView, View},
};

#[derive(Clone)]
pub struct ModelInfo {
    pub id: String,
    pub model: String,
    pub display_name: String,
    pub description: String,
    pub efforts: Vec<EffortInfo>,
    pub default_effort: String,
    pub is_default: bool,
}

#[derive(Clone)]
pub struct EffortInfo {
    pub id: String,
}

impl ModelInfo {
    pub fn from_value(value: &Value) -> Option<Self> {
        let efforts = value
            .get("supportedReasoningEfforts")?
            .as_array()?
            .iter()
            .filter_map(|entry| {
                Some(EffortInfo {
                    id: entry.get("reasoningEffort")?.as_str()?.to_owned(),
                })
            })
            .collect::<Vec<_>>();
        Some(Self {
            id: value.get("id")?.as_str()?.to_owned(),
            model: value.get("model")?.as_str()?.to_owned(),
            display_name: value.get("displayName")?.as_str()?.to_owned(),
            description: value
                .get("description")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned(),
            default_effort: value.get("defaultReasoningEffort")?.as_str()?.to_owned(),
            efforts,
            is_default: value
                .get("isDefault")
                .and_then(Value::as_bool)
                .unwrap_or(false),
        })
    }

    pub fn supports_effort(&self, effort: &str) -> bool {
        self.efforts.iter().any(|candidate| candidate.id == effort)
    }
}

pub enum Action {
    None,
    Submit(String),
    Steer(String),
    Interrupt,
    NewThread,
    Quit,
    ClearScreen,
    RpcResponse { id: Value, result: Value },
    RpcError { id: Value, message: String },
}

struct ActiveItem {
    block: Block,
}

enum PendingInteraction {
    ModelPicker {
        model_index: usize,
        effort_index: usize,
    },
    Approval {
        id: Value,
        title: String,
        detail: Vec<String>,
        once: Value,
        session: Option<Value>,
        decline: Value,
    },
    UserInput {
        id: Value,
        questions: Vec<Question>,
        current: usize,
        selected: usize,
        text_mode: bool,
        editor: Editor,
        answers: BTreeMap<String, String>,
    },
}

struct Question {
    id: String,
    header: String,
    question: String,
    options: Vec<QuestionOption>,
    allow_other: bool,
}

struct QuestionOption {
    label: String,
    description: String,
}

pub struct AppState {
    pub editor: Editor,
    pub thread_id: String,
    pub turn_id: Option<String>,
    pub busy: bool,
    pub cwd: String,
    models: Vec<ModelInfo>,
    selected_model: usize,
    selected_effort: String,
    committed: Vec<Block>,
    active_order: Vec<String>,
    active: HashMap<String, ActiveItem>,
    pending: Option<PendingInteraction>,
    total_tokens: u64,
    context_window: Option<u64>,
    transient_status: Option<String>,
}

impl AppState {
    pub fn new(
        thread_id: String,
        cwd: String,
        models: Vec<ModelInfo>,
        model: &str,
        effort: Option<&str>,
    ) -> Self {
        let selected_model = models
            .iter()
            .position(|candidate| candidate.id == model || candidate.model == model)
            .or_else(|| models.iter().position(|candidate| candidate.is_default))
            .unwrap_or(0);
        let selected_effort = models
            .get(selected_model)
            .map(|selected| {
                effort
                    .filter(|effort| selected.supports_effort(effort))
                    .unwrap_or(&selected.default_effort)
                    .to_owned()
            })
            .or_else(|| effort.map(ToOwned::to_owned))
            .unwrap_or_else(|| "high".to_owned());

        Self {
            editor: Editor::default(),
            thread_id,
            turn_id: None,
            busy: false,
            cwd,
            models,
            selected_model,
            selected_effort,
            committed: vec![Block::new(
                BlockKind::System,
                "Devez CLI",
                "공식 Codex app-server에 연결되었습니다. /help로 명령을 확인하세요.",
            )],
            active_order: Vec::new(),
            active: HashMap::new(),
            pending: None,
            total_tokens: 0,
            context_window: None,
            transient_status: None,
        }
    }

    pub fn selected_model(&self) -> Option<&ModelInfo> {
        self.models.get(self.selected_model)
    }

    pub fn selected_model_name(&self) -> &str {
        self.selected_model()
            .map(|model| model.model.as_str())
            .unwrap_or("default")
    }

    pub fn selected_effort(&self) -> &str {
        &self.selected_effort
    }

    pub fn set_thread(
        &mut self,
        thread_id: String,
        cwd: String,
        model: &str,
        effort: Option<&str>,
    ) {
        self.thread_id = thread_id;
        self.cwd = cwd;
        self.turn_id = None;
        self.busy = false;
        self.active.clear();
        self.active_order.clear();
        if let Some(index) = self
            .models
            .iter()
            .position(|candidate| candidate.id == model || candidate.model == model)
        {
            self.selected_model = index;
        }
        if let Some(effort) = effort
            && self
                .selected_model()
                .is_none_or(|model| model.supports_effort(effort))
        {
            self.selected_effort = effort.to_owned();
        }
    }

    pub fn load_history(&mut self, thread: &Value) {
        let Some(turns) = thread.get("turns").and_then(Value::as_array) else {
            return;
        };
        for turn in turns {
            let Some(items) = turn.get("items").and_then(Value::as_array) else {
                continue;
            };
            for item in items {
                if let Some(block) = completed_item_block(item) {
                    self.committed.push(block);
                }
            }
        }
    }

    pub fn set_turn_started(&mut self, turn_id: String) {
        self.turn_id = Some(turn_id);
        self.busy = true;
    }

    pub fn set_request_failed(&mut self, message: impl Into<String>) {
        self.busy = false;
        self.turn_id = None;
        self.committed
            .push(Block::new(BlockKind::Error, "요청 실패", message));
    }

    pub fn push_notice(
        &mut self,
        kind: BlockKind,
        title: impl Into<String>,
        body: impl Into<String>,
    ) {
        self.committed.push(Block::new(kind, title, body));
    }

    pub fn drain_committed(&mut self) -> Vec<Block> {
        std::mem::take(&mut self.committed)
    }

    pub fn view(&self) -> View<'_> {
        let live_blocks = self
            .active_order
            .iter()
            .filter_map(|id| self.active.get(id))
            .map(|item| item.block.clone())
            .collect::<Vec<_>>();
        View {
            live_blocks,
            overlay: self.overlay_view(),
            editor: &self.editor,
            footer: self.footer(),
        }
    }

    pub fn handle_paste(&mut self, text: &str) {
        match &mut self.pending {
            Some(PendingInteraction::UserInput {
                text_mode: true,
                editor,
                ..
            }) => editor.insert_str(text),
            Some(_) => {}
            None => self.editor.insert_str(text),
        }
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> Action {
        if !matches!(key.kind, KeyEventKind::Press | KeyEventKind::Repeat) {
            return Action::None;
        }
        if self.pending.is_some() {
            return self.handle_pending_key(key);
        }

        let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);
        let alt = key.modifiers.contains(KeyModifiers::ALT);
        let shift = key.modifiers.contains(KeyModifiers::SHIFT);
        match key.code {
            KeyCode::Char('c') if ctrl => {
                if self.busy {
                    Action::Interrupt
                } else if self.editor.is_empty() {
                    Action::Quit
                } else {
                    self.editor.clear();
                    Action::None
                }
            }
            KeyCode::Char('d') if ctrl && self.editor.is_empty() && !self.busy => Action::Quit,
            KeyCode::Char('l') if ctrl => Action::ClearScreen,
            KeyCode::Char('a') if ctrl => {
                self.editor.move_home();
                Action::None
            }
            KeyCode::Char('e') if ctrl => {
                self.editor.move_end();
                Action::None
            }
            KeyCode::Char('w') if ctrl => {
                self.editor.move_word_left();
                Action::None
            }
            KeyCode::Enter if alt || shift => {
                self.editor.newline();
                Action::None
            }
            KeyCode::Enter => self.submit_editor(),
            KeyCode::Esc if self.busy => Action::Interrupt,
            KeyCode::Backspace => {
                self.editor.backspace();
                Action::None
            }
            KeyCode::Delete => {
                self.editor.delete();
                Action::None
            }
            KeyCode::Left if alt || ctrl => {
                self.editor.move_word_left();
                Action::None
            }
            KeyCode::Right if alt || ctrl => {
                self.editor.move_word_right();
                Action::None
            }
            KeyCode::Left => {
                self.editor.move_left();
                Action::None
            }
            KeyCode::Right => {
                self.editor.move_right();
                Action::None
            }
            KeyCode::Home => {
                self.editor.move_home();
                Action::None
            }
            KeyCode::End => {
                self.editor.move_end();
                Action::None
            }
            KeyCode::Up => {
                self.editor.history_previous();
                Action::None
            }
            KeyCode::Down => {
                self.editor.history_next();
                Action::None
            }
            KeyCode::Char(ch) if !ctrl => {
                self.editor.insert(ch);
                Action::None
            }
            _ => Action::None,
        }
    }

    pub fn begin_server_request(&mut self, id: Value, method: &str, params: &Value) -> Action {
        if self.pending.is_some() {
            return Action::RpcError {
                id,
                message: "다른 사용자 입력을 처리 중입니다.".to_owned(),
            };
        }

        match method {
            "item/commandExecution/requestApproval" => {
                let command = params
                    .get("command")
                    .and_then(Value::as_str)
                    .unwrap_or("명령 실행");
                let mut detail = vec![command.to_owned()];
                if let Some(cwd) = params.get("cwd").and_then(Value::as_str) {
                    detail.push(format!("위치: {cwd}"));
                }
                if let Some(reason) = params.get("reason").and_then(Value::as_str) {
                    detail.push(format!("이유: {reason}"));
                }
                self.pending = Some(PendingInteraction::Approval {
                    id,
                    title: "명령 실행을 허용할까요?".to_owned(),
                    detail,
                    once: json!({ "decision": "accept" }),
                    session: Some(json!({ "decision": "acceptForSession" })),
                    decline: json!({ "decision": "decline" }),
                });
                Action::None
            }
            "item/fileChange/requestApproval" => {
                let mut detail = Vec::new();
                if let Some(reason) = params.get("reason").and_then(Value::as_str) {
                    detail.push(reason.to_owned());
                }
                if let Some(root) = params.get("grantRoot").and_then(Value::as_str) {
                    detail.push(format!("쓰기 경로: {root}"));
                }
                self.pending = Some(PendingInteraction::Approval {
                    id,
                    title: "파일 변경을 허용할까요?".to_owned(),
                    detail,
                    once: json!({ "decision": "accept" }),
                    session: Some(json!({ "decision": "acceptForSession" })),
                    decline: json!({ "decision": "decline" }),
                });
                Action::None
            }
            "item/permissions/requestApproval" => {
                let requested = params
                    .get("permissions")
                    .cloned()
                    .unwrap_or_else(|| json!({}));
                let detail = permission_detail(&requested);
                self.pending = Some(PendingInteraction::Approval {
                    id,
                    title: "추가 권한을 허용할까요?".to_owned(),
                    detail,
                    once: json!({ "permissions": requested, "scope": "turn" }),
                    session: Some(json!({
                        "permissions": params.get("permissions").cloned().unwrap_or_else(|| json!({})),
                        "scope": "session"
                    })),
                    decline: json!({ "permissions": {}, "scope": "turn" }),
                });
                Action::None
            }
            "item/tool/requestUserInput" => {
                let questions = parse_questions(params);
                if questions.is_empty() {
                    return Action::RpcResponse {
                        id,
                        result: json!({ "answers": {} }),
                    };
                }
                let text_mode = questions[0].options.is_empty();
                self.pending = Some(PendingInteraction::UserInput {
                    id,
                    questions,
                    current: 0,
                    selected: 0,
                    text_mode,
                    editor: Editor::default(),
                    answers: BTreeMap::new(),
                });
                Action::None
            }
            "mcpServer/elicitation/request" => {
                self.committed.push(Block::new(
                    BlockKind::Warning,
                    "MCP 입력 요청",
                    "이 초기 버전은 MCP 폼 입력을 아직 지원하지 않아 요청을 취소했습니다.",
                ));
                Action::RpcResponse {
                    id,
                    result: json!({ "action": "cancel", "content": null, "_meta": null }),
                }
            }
            _ => Action::RpcError {
                id,
                message: format!("지원하지 않는 서버 요청: {method}"),
            },
        }
    }

    pub fn handle_notification(&mut self, method: &str, params: &Value) {
        match method {
            "turn/started" => {
                if let Some(turn_id) = params
                    .get("turn")
                    .and_then(|turn| turn.get("id"))
                    .and_then(Value::as_str)
                {
                    self.set_turn_started(turn_id.to_owned());
                }
            }
            "turn/completed" => {
                self.busy = false;
                self.turn_id = None;
                if let Some(error) = params
                    .get("turn")
                    .and_then(|turn| turn.get("error"))
                    .filter(|error| !error.is_null())
                {
                    self.committed.push(Block::new(
                        BlockKind::Error,
                        "Turn 실패",
                        error
                            .get("message")
                            .and_then(Value::as_str)
                            .unwrap_or("알 수 없는 오류"),
                    ));
                }
                self.flush_orphaned_active();
            }
            "item/started" => {
                if let Some(item) = params.get("item") {
                    self.start_item(item);
                }
            }
            "item/completed" => {
                if let Some(item) = params.get("item") {
                    self.complete_item(item);
                }
            }
            "item/agentMessage/delta" => {
                self.append_delta(params, BlockKind::Assistant, "Codex");
            }
            "item/reasoning/summaryTextDelta" => {
                self.append_delta(params, BlockKind::Reasoning, "Thinking…");
            }
            "item/commandExecution/outputDelta" => {
                self.append_delta(params, BlockKind::Tool, "Command");
            }
            "item/plan/delta" => {
                self.append_delta(params, BlockKind::Reasoning, "Plan");
            }
            "item/fileChange/patchUpdated" => {
                if let Some(item_id) = params.get("itemId").and_then(Value::as_str) {
                    let body = file_changes_body(
                        params
                            .get("changes")
                            .and_then(Value::as_array)
                            .map(Vec::as_slice)
                            .unwrap_or(&[]),
                    );
                    self.ensure_active(item_id, BlockKind::Tool, "Files")
                        .block
                        .body = body;
                }
            }
            "item/mcpToolCall/progress" => {
                if let Some(item_id) = params.get("itemId").and_then(Value::as_str)
                    && let Some(message) = params.get("message").and_then(Value::as_str)
                {
                    append_capped(
                        &mut self
                            .ensure_active(item_id, BlockKind::Tool, "MCP")
                            .block
                            .body,
                        message,
                    );
                }
            }
            "thread/tokenUsage/updated" => {
                if let Some(usage) = params.get("tokenUsage") {
                    self.total_tokens = usage
                        .get("total")
                        .and_then(|total| total.get("totalTokens"))
                        .and_then(Value::as_u64)
                        .unwrap_or(self.total_tokens);
                    self.context_window = usage.get("modelContextWindow").and_then(Value::as_u64);
                }
            }
            "error" => {
                let message = params
                    .get("error")
                    .and_then(|error| error.get("message"))
                    .and_then(Value::as_str)
                    .unwrap_or("알 수 없는 Codex 오류");
                let retry = params
                    .get("willRetry")
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                self.committed.push(Block::new(
                    if retry {
                        BlockKind::Warning
                    } else {
                        BlockKind::Error
                    },
                    if retry {
                        "재시도 중"
                    } else {
                        "Codex 오류"
                    },
                    message,
                ));
            }
            "warning" | "configWarning" | "guardianWarning" | "deprecationNotice" => {
                let message = params
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or_else(|| params.as_str().unwrap_or("Codex 경고"));
                self.committed
                    .push(Block::new(BlockKind::Warning, "경고", message));
            }
            "model/rerouted" => {
                if let (Some(from), Some(to)) = (
                    params.get("fromModel").and_then(Value::as_str),
                    params.get("toModel").and_then(Value::as_str),
                ) {
                    self.transient_status = Some(format!("{from} → {to}로 전환됨"));
                }
            }
            "thread/compacted" => self.committed.push(Block::new(
                BlockKind::System,
                "Context compacted",
                "대화 컨텍스트가 압축되었습니다.",
            )),
            _ => {}
        }
    }

    fn submit_editor(&mut self) -> Action {
        let Some(text) = self.editor.take_for_submit() else {
            return Action::None;
        };
        if text.starts_with('/') && !text.contains('\n') {
            return self.run_slash_command(&text);
        }
        self.committed
            .push(Block::new(BlockKind::User, "You", text.clone()));
        if self.busy {
            Action::Steer(text)
        } else {
            self.busy = true;
            Action::Submit(text)
        }
    }

    fn run_slash_command(&mut self, command: &str) -> Action {
        let parts = command.split_whitespace().collect::<Vec<_>>();
        match parts.first().copied().unwrap_or_default() {
            "/help" => {
                self.committed.push(Block::new(
                    BlockKind::System,
                    "Commands",
                    "/model [MODEL] [EFFORT]  모델 선택\n/effort EFFORT  추론 수준\n/new  새 대화\n/status  현재 설정\n/clear  화면 정리\n/quit  종료\n\nEsc 또는 Ctrl+C  실행 중단\nAlt+Enter  줄바꿈",
                ));
                Action::None
            }
            "/model" if parts.len() == 1 => {
                let effort_index = self
                    .selected_model()
                    .and_then(|model| {
                        model
                            .efforts
                            .iter()
                            .position(|effort| effort.id == self.selected_effort)
                    })
                    .unwrap_or(0);
                self.pending = Some(PendingInteraction::ModelPicker {
                    model_index: self.selected_model,
                    effort_index,
                });
                Action::None
            }
            "/model" => {
                let query = parts[1];
                let Some(index) = self.models.iter().position(|candidate| {
                    candidate.id == query
                        || candidate.model == query
                        || candidate.display_name.eq_ignore_ascii_case(query)
                }) else {
                    self.committed
                        .push(Block::new(BlockKind::Error, "모델을 찾을 수 없음", query));
                    return Action::None;
                };
                let effort = parts.get(2).copied();
                self.apply_model(index, effort);
                Action::None
            }
            "/effort" if parts.len() == 2 => {
                let effort = parts[1];
                if self
                    .selected_model()
                    .is_some_and(|model| model.supports_effort(effort))
                {
                    self.selected_effort = effort.to_owned();
                    self.committed
                        .push(Block::new(BlockKind::Success, "Reasoning 변경", effort));
                } else {
                    self.committed.push(Block::new(
                        BlockKind::Error,
                        "지원하지 않는 reasoning effort",
                        effort,
                    ));
                }
                Action::None
            }
            "/new" if self.busy => {
                self.committed.push(Block::new(
                    BlockKind::Warning,
                    "진행 중",
                    "현재 응답을 중단한 뒤 새 대화를 시작하세요.",
                ));
                Action::None
            }
            "/new" => Action::NewThread,
            "/status" => {
                let model = self.selected_model_name();
                self.committed.push(Block::new(
                    BlockKind::System,
                    "Status",
                    format!(
                        "thread: {}\nmodel: {model}\neffort: {}\ncwd: {}",
                        self.thread_id, self.selected_effort, self.cwd
                    ),
                ));
                Action::None
            }
            "/clear" => Action::ClearScreen,
            "/quit" | "/exit" => Action::Quit,
            unknown => {
                self.committed.push(Block::new(
                    BlockKind::Error,
                    "알 수 없는 명령",
                    format!("{unknown} — /help로 목록을 확인하세요."),
                ));
                Action::None
            }
        }
    }

    fn handle_pending_key(&mut self, key: KeyEvent) -> Action {
        let pending = self.pending.take().expect("pending checked");
        match pending {
            PendingInteraction::ModelPicker {
                mut model_index,
                mut effort_index,
            } => {
                match key.code {
                    KeyCode::Esc => return Action::None,
                    KeyCode::Up => {
                        model_index = model_index.saturating_sub(1);
                        effort_index = 0;
                    }
                    KeyCode::Down => {
                        model_index = (model_index + 1).min(self.models.len().saturating_sub(1));
                        effort_index = 0;
                    }
                    KeyCode::Left => {
                        effort_index = effort_index.saturating_sub(1);
                    }
                    KeyCode::Right | KeyCode::Tab => {
                        let count = self
                            .models
                            .get(model_index)
                            .map(|model| model.efforts.len())
                            .unwrap_or(1)
                            .max(1);
                        effort_index = (effort_index + 1) % count;
                    }
                    KeyCode::Enter => {
                        let effort = self
                            .models
                            .get(model_index)
                            .and_then(|model| model.efforts.get(effort_index))
                            .map(|effort| effort.id.clone());
                        self.apply_model(model_index, effort.as_deref());
                        return Action::None;
                    }
                    _ => {}
                }
                self.pending = Some(PendingInteraction::ModelPicker {
                    model_index,
                    effort_index,
                });
                Action::None
            }
            PendingInteraction::Approval {
                id,
                title,
                detail,
                once,
                session,
                decline,
            } => match key.code {
                KeyCode::Char('y') | KeyCode::Enter => Action::RpcResponse { id, result: once },
                KeyCode::Char('a') if session.is_some() => Action::RpcResponse {
                    id,
                    result: session.expect("checked"),
                },
                KeyCode::Char('n') | KeyCode::Esc => Action::RpcResponse {
                    id,
                    result: decline,
                },
                _ => {
                    self.pending = Some(PendingInteraction::Approval {
                        id,
                        title,
                        detail,
                        once,
                        session,
                        decline,
                    });
                    Action::None
                }
            },
            PendingInteraction::UserInput {
                id,
                questions,
                current,
                mut selected,
                mut text_mode,
                mut editor,
                mut answers,
            } => {
                if key.code == KeyCode::Esc {
                    return Action::RpcResponse {
                        id,
                        result: answers_response(&answers),
                    };
                }

                let question = &questions[current];
                if text_mode {
                    match key.code {
                        KeyCode::Enter => {
                            let answer = editor.take_for_submit().unwrap_or_default();
                            answers.insert(question.id.clone(), answer);
                            return next_question_or_reply(id, questions, current, answers, self);
                        }
                        KeyCode::Backspace => editor.backspace(),
                        KeyCode::Delete => editor.delete(),
                        KeyCode::Left => editor.move_left(),
                        KeyCode::Right => editor.move_right(),
                        KeyCode::Home => editor.move_home(),
                        KeyCode::End => editor.move_end(),
                        KeyCode::Char(ch) if !key.modifiers.contains(KeyModifiers::CONTROL) => {
                            editor.insert(ch);
                        }
                        _ => {}
                    }
                } else {
                    let option_count = question.options.len() + usize::from(question.allow_other);
                    match key.code {
                        KeyCode::Up => selected = selected.saturating_sub(1),
                        KeyCode::Down => {
                            selected = (selected + 1).min(option_count.saturating_sub(1))
                        }
                        KeyCode::Enter => {
                            if selected < question.options.len() {
                                answers.insert(
                                    question.id.clone(),
                                    question.options[selected].label.clone(),
                                );
                                return next_question_or_reply(
                                    id, questions, current, answers, self,
                                );
                            }
                            text_mode = true;
                        }
                        _ => {}
                    }
                }
                self.pending = Some(PendingInteraction::UserInput {
                    id,
                    questions,
                    current,
                    selected,
                    text_mode,
                    editor,
                    answers,
                });
                Action::None
            }
        }
    }

    fn overlay_view(&self) -> Option<OverlayView<'_>> {
        match self.pending.as_ref()? {
            PendingInteraction::ModelPicker {
                model_index,
                effort_index,
            } => {
                let start = model_index.saturating_sub(4);
                let end = (start + 9).min(self.models.len());
                let lines = self.models[start..end]
                    .iter()
                    .enumerate()
                    .map(|(offset, model)| {
                        let index = start + offset;
                        let effort = if index == *model_index {
                            model
                                .efforts
                                .get(*effort_index)
                                .map(|effort| effort.id.as_str())
                                .unwrap_or(model.default_effort.as_str())
                        } else {
                            model.default_effort.as_str()
                        };
                        OverlayLine {
                            text: format!(
                                "{}  [{}]\n      {}",
                                model.display_name, effort, model.description
                            ),
                            selected: index == *model_index,
                            muted: false,
                        }
                    })
                    .collect();
                Some(OverlayView {
                    title: "Model".to_owned(),
                    lines,
                    hint: "↑↓ 모델  ←→ reasoning  Enter 적용  Esc 취소".to_owned(),
                    input: None,
                })
            }
            PendingInteraction::Approval {
                title,
                detail,
                session,
                ..
            } => {
                let mut lines = detail
                    .iter()
                    .map(|text| OverlayLine {
                        text: text.clone(),
                        selected: false,
                        muted: false,
                    })
                    .collect::<Vec<_>>();
                lines.push(OverlayLine {
                    text: "[y] 이번만 허용".to_owned(),
                    selected: true,
                    muted: false,
                });
                if session.is_some() {
                    lines.push(OverlayLine {
                        text: "[a] 세션 동안 허용".to_owned(),
                        selected: false,
                        muted: false,
                    });
                }
                lines.push(OverlayLine {
                    text: "[n] 거부".to_owned(),
                    selected: false,
                    muted: false,
                });
                Some(OverlayView {
                    title: title.clone(),
                    lines,
                    hint: "y / a / n".to_owned(),
                    input: None,
                })
            }
            PendingInteraction::UserInput {
                questions,
                current,
                selected,
                text_mode,
                editor,
                ..
            } => {
                let question = &questions[*current];
                let mut lines = vec![OverlayLine {
                    text: question.question.clone(),
                    selected: false,
                    muted: false,
                }];
                if !text_mode {
                    lines.extend(question.options.iter().enumerate().map(|(index, option)| {
                        OverlayLine {
                            text: format!("{}\n      {}", option.label, option.description),
                            selected: index == *selected,
                            muted: false,
                        }
                    }));
                    if question.allow_other {
                        lines.push(OverlayLine {
                            text: "직접 입력".to_owned(),
                            selected: *selected == question.options.len(),
                            muted: false,
                        });
                    }
                }
                Some(OverlayView {
                    title: if question.header.is_empty() {
                        format!("Question {}/{}", current + 1, questions.len())
                    } else {
                        question.header.clone()
                    },
                    lines,
                    hint: if *text_mode {
                        "답을 입력하고 Enter · Esc 취소".to_owned()
                    } else {
                        "↑↓ 선택  Enter 확인  Esc 취소".to_owned()
                    },
                    input: text_mode.then_some(editor),
                })
            }
        }
    }

    fn footer(&self) -> String {
        let model = self.selected_model_name();
        let usage = match self.context_window {
            Some(window) if window > 0 => {
                format!(" · {}%", self.total_tokens.saturating_mul(100) / window)
            }
            _ if self.total_tokens > 0 => format!(" · {} tok", self.total_tokens),
            _ => String::new(),
        };
        let activity = if self.busy { " · Esc 중단" } else { "" };
        let transient = self
            .transient_status
            .as_ref()
            .map(|message| format!(" · {message}"))
            .unwrap_or_default();
        format!(
            "{} · {} ({}){}{}{}",
            compact_path(&self.cwd, 36),
            model,
            self.selected_effort,
            usage,
            activity,
            transient
        )
    }

    fn apply_model(&mut self, index: usize, effort: Option<&str>) {
        let Some(model) = self.models.get(index) else {
            return;
        };
        let selected_effort = effort
            .filter(|effort| model.supports_effort(effort))
            .unwrap_or(&model.default_effort)
            .to_owned();
        let model_name = model.display_name.clone();
        self.selected_model = index;
        self.selected_effort = selected_effort.clone();
        self.committed.push(Block::new(
            BlockKind::Success,
            "Model 변경",
            format!("{model_name} · {selected_effort}"),
        ));
    }

    fn start_item(&mut self, item: &Value) {
        let Some(id) = item.get("id").and_then(Value::as_str) else {
            return;
        };
        let Some(block) = active_item_block(item) else {
            return;
        };
        if !self.active.contains_key(id) {
            self.active_order.push(id.to_owned());
        }
        self.active.insert(id.to_owned(), ActiveItem { block });
    }

    fn complete_item(&mut self, item: &Value) {
        let id = item.get("id").and_then(Value::as_str);
        if let Some(id) = id {
            self.active.remove(id);
            self.active_order.retain(|candidate| candidate != id);
        }
        if item.get("type").and_then(Value::as_str) == Some("userMessage") {
            return;
        }
        if let Some(block) = completed_item_block(item) {
            self.committed.push(block);
        }
    }

    fn append_delta(&mut self, params: &Value, kind: BlockKind, title: &str) {
        let Some(item_id) = params.get("itemId").and_then(Value::as_str) else {
            return;
        };
        let Some(delta) = params.get("delta").and_then(Value::as_str) else {
            return;
        };
        append_capped(
            &mut self.ensure_active(item_id, kind, title).block.body,
            delta,
        );
    }

    fn ensure_active(&mut self, item_id: &str, kind: BlockKind, title: &str) -> &mut ActiveItem {
        if !self.active.contains_key(item_id) {
            self.active_order.push(item_id.to_owned());
            self.active.insert(
                item_id.to_owned(),
                ActiveItem {
                    block: Block::new(kind, title, ""),
                },
            );
        }
        self.active.get_mut(item_id).expect("inserted")
    }

    fn flush_orphaned_active(&mut self) {
        for id in std::mem::take(&mut self.active_order) {
            if let Some(item) = self.active.remove(&id) {
                self.committed.push(item.block);
            }
        }
    }
}

fn next_question_or_reply(
    id: Value,
    questions: Vec<Question>,
    current: usize,
    answers: BTreeMap<String, String>,
    state: &mut AppState,
) -> Action {
    if current + 1 == questions.len() {
        return Action::RpcResponse {
            id,
            result: answers_response(&answers),
        };
    }
    let next = current + 1;
    let text_mode = questions[next].options.is_empty();
    state.pending = Some(PendingInteraction::UserInput {
        id,
        questions,
        current: next,
        selected: 0,
        text_mode,
        editor: Editor::default(),
        answers,
    });
    Action::None
}

fn answers_response(answers: &BTreeMap<String, String>) -> Value {
    let mut map = Map::new();
    for (id, answer) in answers {
        map.insert(id.clone(), json!({ "answers": [answer] }));
    }
    json!({ "answers": map })
}

fn parse_questions(params: &Value) -> Vec<Question> {
    params
        .get("questions")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|question| {
            let options = question
                .get("options")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|option| {
                    Some(QuestionOption {
                        label: option.get("label")?.as_str()?.to_owned(),
                        description: option
                            .get("description")
                            .and_then(Value::as_str)
                            .unwrap_or_default()
                            .to_owned(),
                    })
                })
                .collect();
            Some(Question {
                id: question.get("id")?.as_str()?.to_owned(),
                header: question
                    .get("header")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_owned(),
                question: question.get("question")?.as_str()?.to_owned(),
                options,
                allow_other: question
                    .get("isOther")
                    .and_then(Value::as_bool)
                    .unwrap_or(true),
            })
        })
        .collect()
}

fn active_item_block(item: &Value) -> Option<Block> {
    match item.get("type")?.as_str()? {
        "agentMessage" => Some(Block::new(
            BlockKind::Assistant,
            "Codex",
            item.get("text").and_then(Value::as_str).unwrap_or_default(),
        )),
        "reasoning" => Some(Block::new(
            BlockKind::Reasoning,
            "Thinking…",
            string_array(item.get("summary")),
        )),
        "plan" => Some(Block::new(
            BlockKind::Reasoning,
            "Plan",
            item.get("text").and_then(Value::as_str).unwrap_or_default(),
        )),
        "commandExecution" => Some(Block::new(
            BlockKind::Tool,
            format!(
                "Bash · {}",
                compact_command(
                    item.get("command")
                        .and_then(Value::as_str)
                        .unwrap_or("command"),
                    88
                )
            ),
            item.get("aggregatedOutput")
                .and_then(Value::as_str)
                .unwrap_or_default(),
        )),
        "fileChange" => Some(Block::new(
            BlockKind::Tool,
            "Update files",
            file_changes_body(
                item.get("changes")
                    .and_then(Value::as_array)
                    .map(Vec::as_slice)
                    .unwrap_or(&[]),
            ),
        )),
        "mcpToolCall" => Some(Block::new(
            BlockKind::Tool,
            format!(
                "MCP · {} › {}",
                item.get("server")
                    .and_then(Value::as_str)
                    .unwrap_or("server"),
                item.get("tool").and_then(Value::as_str).unwrap_or("tool")
            ),
            pretty_json(item.get("arguments")),
        )),
        "dynamicToolCall" => Some(Block::new(
            BlockKind::Tool,
            format!(
                "Tool · {}",
                item.get("tool").and_then(Value::as_str).unwrap_or("tool")
            ),
            pretty_json(item.get("arguments")),
        )),
        "webSearch" => Some(Block::new(BlockKind::Tool, "Web search", "")),
        "collabAgentToolCall" => Some(Block::new(
            BlockKind::Tool,
            "Agent",
            item.get("tool").map(Value::to_string).unwrap_or_default(),
        )),
        _ => None,
    }
}

fn completed_item_block(item: &Value) -> Option<Block> {
    match item.get("type")?.as_str()? {
        "userMessage" => {
            let body = item
                .get("content")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|content| {
                    (content.get("type").and_then(Value::as_str) == Some("text"))
                        .then(|| content.get("text").and_then(Value::as_str))
                        .flatten()
                })
                .collect::<Vec<_>>()
                .join("\n");
            (!body.is_empty()).then(|| Block::new(BlockKind::User, "You", body))
        }
        "commandExecution" => {
            let status = item
                .get("status")
                .and_then(Value::as_str)
                .unwrap_or("completed");
            let exit = item.get("exitCode").and_then(Value::as_i64);
            let suffix = exit
                .map(|code| format!(" · exit {code}"))
                .unwrap_or_default();
            Some(Block::new(
                if status == "completed" {
                    BlockKind::Tool
                } else {
                    BlockKind::Warning
                },
                format!(
                    "Bash · {}{suffix}",
                    compact_command(
                        item.get("command")
                            .and_then(Value::as_str)
                            .unwrap_or("command"),
                        88
                    )
                ),
                collapse_output(
                    item.get("aggregatedOutput")
                        .and_then(Value::as_str)
                        .unwrap_or_default(),
                    14,
                ),
            ))
        }
        "fileChange" => Some(Block::new(
            BlockKind::Tool,
            "Updated files",
            file_changes_body(
                item.get("changes")
                    .and_then(Value::as_array)
                    .map(Vec::as_slice)
                    .unwrap_or(&[]),
            ),
        )),
        "mcpToolCall" => {
            let mut block = active_item_block(item)?;
            block.body = match item.get("error").filter(|value| !value.is_null()) {
                Some(error) => pretty_json(Some(error)),
                None => pretty_json(item.get("result")),
            };
            Some(block)
        }
        "dynamicToolCall" => {
            let mut block = active_item_block(item)?;
            block.body = pretty_json(item.get("contentItems"));
            Some(block)
        }
        "contextCompaction" => Some(Block::new(BlockKind::System, "Context compacted", "")),
        _ => active_item_block(item),
    }
}

fn permission_detail(value: &Value) -> Vec<String> {
    let mut detail = Vec::new();
    if let Some(enabled) = value
        .get("network")
        .and_then(|network| network.get("enabled"))
        .and_then(Value::as_bool)
    {
        detail.push(format!(
            "네트워크: {}",
            if enabled { "허용" } else { "차단" }
        ));
    }
    if let Some(file_system) = value.get("fileSystem").filter(|value| !value.is_null()) {
        detail.push(format!("파일 시스템: {}", pretty_json(Some(file_system))));
    }
    if detail.is_empty() {
        detail.push(pretty_json(Some(value)));
    }
    detail
}

fn file_changes_body(changes: &[Value]) -> String {
    changes
        .iter()
        .map(|change| {
            let path = change
                .get("path")
                .and_then(Value::as_str)
                .unwrap_or("unknown");
            let kind = change
                .get("kind")
                .and_then(|kind| kind.get("type"))
                .and_then(Value::as_str)
                .unwrap_or("update");
            format!("{kind:>6}  {path}")
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn append_capped(target: &mut String, delta: &str) {
    const MAX_ACTIVE_BYTES: usize = 128 * 1024;
    target.push_str(delta);
    if target.len() > MAX_ACTIVE_BYTES {
        let keep_from = target.len() - MAX_ACTIVE_BYTES;
        let boundary = target
            .char_indices()
            .map(|(index, _)| index)
            .find(|index| *index >= keep_from)
            .unwrap_or(keep_from);
        target.replace_range(..boundary, "…\n");
    }
}

fn collapse_output(output: &str, max_lines: usize) -> String {
    let lines = output.lines().collect::<Vec<_>>();
    if lines.len() <= max_lines {
        return output.trim_end().to_owned();
    }
    let head = max_lines / 2;
    let tail = max_lines - head;
    format!(
        "{}\n… {} lines hidden …\n{}",
        lines[..head].join("\n"),
        lines.len() - max_lines,
        lines[lines.len() - tail..].join("\n")
    )
}

fn string_array(value: Option<&Value>) -> String {
    value
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .collect::<Vec<_>>()
        .join("\n")
}

fn pretty_json(value: Option<&Value>) -> String {
    let Some(value) = value.filter(|value| !value.is_null()) else {
        return String::new();
    };
    serde_json::to_string_pretty(value).unwrap_or_else(|_| value.to_string())
}

fn compact_command(command: &str, max_chars: usize) -> String {
    let one_line = command.split_whitespace().collect::<Vec<_>>().join(" ");
    if one_line.chars().count() <= max_chars {
        return one_line;
    }
    format!(
        "{}…",
        one_line
            .chars()
            .take(max_chars.saturating_sub(1))
            .collect::<String>()
    )
}

fn compact_path(path: &str, max_chars: usize) -> String {
    let count = path.chars().count();
    if count <= max_chars {
        return path.to_owned();
    }
    format!(
        "…{}",
        path.chars()
            .skip(count - max_chars.saturating_sub(1))
            .collect::<String>()
    )
}
