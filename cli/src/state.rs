use std::{
    collections::{BTreeMap, HashMap},
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};
use serde_json::{Map, Value, json};

use crate::{
    editor::Editor,
    renderer::{
        Block, BlockKind, OverlayLine, OverlayView, StatusLineView, SuggestionView, View,
        WelcomeView,
    },
};

const SPINNER: [&str; 8] = ["✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳"];

struct SlashCommand {
    name: &'static str,
    description: &'static str,
    takes_argument: bool,
}

const SLASH_COMMANDS: [SlashCommand; 9] = [
    SlashCommand {
        name: "/model",
        description: "Switch model and reasoning",
        takes_argument: true,
    },
    SlashCommand {
        name: "/effort",
        description: "Set reasoning effort",
        takes_argument: true,
    },
    SlashCommand {
        name: "/new",
        description: "Start a new thread",
        takes_argument: false,
    },
    SlashCommand {
        name: "/resume",
        description: "Resume a saved session",
        takes_argument: true,
    },
    SlashCommand {
        name: "/continue",
        description: "Alias for /resume",
        takes_argument: false,
    },
    SlashCommand {
        name: "/status",
        description: "Show session details",
        takes_argument: false,
    },
    SlashCommand {
        name: "/clear",
        description: "Clear the terminal",
        takes_argument: false,
    },
    SlashCommand {
        name: "/help",
        description: "Show commands and shortcuts",
        takes_argument: false,
    },
    SlashCommand {
        name: "/quit",
        description: "Exit Devez CLI",
        takes_argument: false,
    },
];

#[derive(Clone)]
pub struct ModelInfo {
    pub id: String,
    pub model: String,
    pub display_name: String,
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
    OpenResume,
    ResumeThread(String),
    Quit,
    ClearScreen,
    Tick,
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
    EffortPicker {
        effort_index: usize,
    },
    SessionPicker(SessionPicker),
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

#[derive(Clone)]
pub struct SessionInfo {
    pub id: String,
    pub name: Option<String>,
    pub preview: String,
    pub cwd: String,
    pub updated_at: u64,
}

impl SessionInfo {
    pub fn from_value(value: &Value) -> Option<Self> {
        Some(Self {
            id: value.get("id")?.as_str()?.to_owned(),
            name: value
                .get("name")
                .and_then(Value::as_str)
                .map(ToOwned::to_owned),
            preview: value
                .get("preview")
                .and_then(Value::as_str)
                .unwrap_or("Untitled session")
                .lines()
                .next()
                .unwrap_or("Untitled session")
                .to_owned(),
            cwd: value
                .get("cwd")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned(),
            updated_at: value
                .get("updatedAt")
                .and_then(Value::as_u64)
                .unwrap_or_default(),
        })
    }

    fn title(&self) -> &str {
        self.name.as_deref().unwrap_or(&self.preview)
    }
}

pub enum SessionPickerResult {
    None,
    Cancel,
    Select(String),
}

pub struct SessionPicker {
    sessions: Vec<SessionInfo>,
    cwd: String,
    current_thread_id: Option<String>,
    selected: usize,
    all_projects: bool,
    query: Editor,
}

impl SessionPicker {
    pub fn new(sessions: Vec<SessionInfo>, cwd: String, current_thread_id: Option<String>) -> Self {
        Self {
            sessions,
            cwd,
            current_thread_id,
            selected: 0,
            all_projects: false,
            query: Editor::default(),
        }
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> SessionPickerResult {
        if !matches!(key.kind, KeyEventKind::Press | KeyEventKind::Repeat) {
            return SessionPickerResult::None;
        }
        let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);
        match key.code {
            KeyCode::Esc => SessionPickerResult::Cancel,
            KeyCode::Char('c') if ctrl => SessionPickerResult::Cancel,
            KeyCode::Char('a') if ctrl => {
                self.all_projects = !self.all_projects;
                self.selected = 0;
                SessionPickerResult::None
            }
            KeyCode::Char('u') if ctrl => {
                self.query.clear();
                self.selected = 0;
                SessionPickerResult::None
            }
            KeyCode::Up => {
                self.selected = self.selected.saturating_sub(1);
                SessionPickerResult::None
            }
            KeyCode::Char('p') if ctrl => {
                self.selected = self.selected.saturating_sub(1);
                SessionPickerResult::None
            }
            KeyCode::Down => {
                self.selected = (self.selected + 1).min(self.filtered_len().saturating_sub(1));
                SessionPickerResult::None
            }
            KeyCode::Char('n') if ctrl => {
                self.selected = (self.selected + 1).min(self.filtered_len().saturating_sub(1));
                SessionPickerResult::None
            }
            KeyCode::PageUp => {
                self.selected = self.selected.saturating_sub(8);
                SessionPickerResult::None
            }
            KeyCode::PageDown => {
                self.selected = (self.selected + 8).min(self.filtered_len().saturating_sub(1));
                SessionPickerResult::None
            }
            KeyCode::Enter => self
                .filtered()
                .get(self.selected)
                .map(|session| SessionPickerResult::Select(session.id.clone()))
                .unwrap_or(SessionPickerResult::None),
            KeyCode::Backspace if ctrl => {
                self.query.delete_word_left();
                self.selected = 0;
                SessionPickerResult::None
            }
            KeyCode::Backspace => {
                self.query.backspace();
                self.selected = 0;
                SessionPickerResult::None
            }
            KeyCode::Delete => {
                self.query.delete();
                self.selected = 0;
                SessionPickerResult::None
            }
            KeyCode::Left => {
                self.query.move_left();
                SessionPickerResult::None
            }
            KeyCode::Right => {
                self.query.move_right();
                SessionPickerResult::None
            }
            KeyCode::Char('b') if key.modifiers.contains(KeyModifiers::ALT) => {
                self.query.move_word_left();
                SessionPickerResult::None
            }
            KeyCode::Char('f') if key.modifiers.contains(KeyModifiers::ALT) => {
                self.query.move_word_right();
                SessionPickerResult::None
            }
            KeyCode::Home => {
                self.query.move_home();
                SessionPickerResult::None
            }
            KeyCode::End => {
                self.query.move_end();
                SessionPickerResult::None
            }
            KeyCode::Char(ch) if !ctrl => {
                self.query.insert(ch);
                self.selected = 0;
                SessionPickerResult::None
            }
            _ => SessionPickerResult::None,
        }
    }

    pub fn handle_paste(&mut self, text: &str) {
        self.query.insert_str(text);
        self.selected = 0;
    }

    pub fn overlay_view(&self) -> OverlayView<'_> {
        let filtered = self.filtered();
        let start = self.selected.saturating_sub(4);
        let end = (start + 9).min(filtered.len());
        let mut lines = filtered[start..end]
            .iter()
            .enumerate()
            .map(|(offset, session)| {
                let index = start + offset;
                let current = self
                    .current_thread_id
                    .as_deref()
                    .is_some_and(|id| id == session.id);
                let path = if self.all_projects {
                    format!("\n      {}", session.cwd)
                } else {
                    String::new()
                };
                OverlayLine {
                    text: format!(
                        "{}  ·  {}{}{}",
                        session.title(),
                        relative_time(session.updated_at),
                        if current { "  ·  current" } else { "" },
                        path
                    ),
                    selected: index == self.selected,
                    muted: false,
                }
            })
            .collect::<Vec<_>>();
        if lines.is_empty() {
            lines.push(OverlayLine {
                text: if self.query.is_empty() {
                    "No sessions found in this folder.".to_owned()
                } else {
                    "No sessions match your search.".to_owned()
                },
                selected: false,
                muted: true,
            });
        }
        OverlayView {
            title: format!(
                "Resume session · {} · {}",
                filtered.len(),
                if self.all_projects {
                    "all projects"
                } else {
                    "this folder"
                }
            ),
            lines,
            hint: "↑↓ navigate  Enter resume  Ctrl+A all projects  Esc cancel".to_owned(),
            input: Some(&self.query),
            input_label: "Search",
            input_placeholder: "Search by name, prompt, ID, or folder…",
        }
    }

    fn filtered(&self) -> Vec<&SessionInfo> {
        let query = self.query.text().to_lowercase();
        self.sessions
            .iter()
            .filter(|session| {
                (self.all_projects || path_eq(&session.cwd, &self.cwd))
                    && (query.is_empty()
                        || session.title().to_lowercase().contains(&query)
                        || session.id.to_lowercase().contains(&query)
                        || session.cwd.to_lowercase().contains(&query))
            })
            .collect()
    }

    fn filtered_len(&self) -> usize {
        self.filtered().len()
    }
}

pub struct AppState {
    pub editor: Editor,
    pub thread_id: String,
    pub turn_id: Option<String>,
    pub busy: bool,
    pub cwd: String,
    account: String,
    models: Vec<ModelInfo>,
    selected_model: usize,
    selected_effort: String,
    effort_is_auto: bool,
    committed: Vec<Block>,
    active_order: Vec<String>,
    active: HashMap<String, ActiveItem>,
    pending: Option<PendingInteraction>,
    total_tokens: u64,
    context_window: Option<u64>,
    transient_status: Option<String>,
    show_welcome: bool,
    command_selection: usize,
    spinner_frame: usize,
    turn_started_at: Option<Instant>,
}

impl AppState {
    pub fn new(
        thread_id: String,
        cwd: String,
        account: String,
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
            account,
            models,
            selected_model,
            selected_effort,
            effort_is_auto: false,
            committed: Vec::new(),
            active_order: Vec::new(),
            active: HashMap::new(),
            pending: None,
            total_tokens: 0,
            context_window: None,
            transient_status: None,
            show_welcome: true,
            command_selection: 0,
            spinner_frame: 0,
            turn_started_at: None,
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

    pub fn selected_model_display_name(&self) -> &str {
        self.selected_model()
            .map(|model| model.display_name.as_str())
            .unwrap_or_else(|| self.selected_model_name())
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
        self.turn_started_at = None;
        self.active.clear();
        self.active_order.clear();
        self.show_welcome = true;
        self.effort_is_auto = false;
        if let Some(index) = self
            .models
            .iter()
            .position(|candidate| candidate.id == model || candidate.model == model)
        {
            self.selected_model = index;
        }
        self.selected_effort = self
            .selected_model()
            .map(|model| {
                effort
                    .filter(|effort| model.supports_effort(effort))
                    .unwrap_or(&model.default_effort)
                    .to_owned()
            })
            .or_else(|| effort.map(ToOwned::to_owned))
            .unwrap_or_else(|| self.selected_effort.clone());
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
        self.show_welcome = false;
    }

    pub fn set_turn_started(&mut self, turn_id: String) {
        self.turn_id = Some(turn_id);
        self.busy = true;
        self.turn_started_at = Some(Instant::now());
    }

    pub fn set_request_failed(&mut self, message: impl Into<String>) {
        self.busy = false;
        self.turn_id = None;
        self.turn_started_at = None;
        self.committed
            .push(Block::new(BlockKind::Error, "요청 실패", message));
    }

    pub fn open_session_picker(&mut self, sessions: Vec<SessionInfo>) {
        self.pending = Some(PendingInteraction::SessionPicker(SessionPicker::new(
            sessions,
            self.cwd.clone(),
            Some(self.thread_id.clone()),
        )));
    }

    pub fn prepare_resume(&mut self) {
        self.committed.clear();
        self.active.clear();
        self.active_order.clear();
        self.pending = None;
        self.total_tokens = 0;
        self.context_window = None;
        self.transient_status = None;
        self.show_welcome = false;
        self.busy = false;
        self.turn_id = None;
        self.turn_started_at = None;
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
            welcome: self.show_welcome.then(|| WelcomeView {
                model: self.selected_model_display_name().to_owned(),
                effort: self.effort_label(),
                cwd: self.cwd.clone(),
                account: self.account.clone(),
            }),
            suggestions: if self.pending.is_none() {
                self.slash_suggestion_views()
            } else {
                Vec::new()
            },
            activity: self.activity(),
            footer: String::new(),
            status_line: Some(self.status_line()),
        }
    }

    pub fn tick(&mut self) {
        if self.busy {
            self.spinner_frame = (self.spinner_frame + 1) % SPINNER.len();
        }
    }

    pub fn handle_paste(&mut self, text: &str) {
        match &mut self.pending {
            Some(PendingInteraction::UserInput {
                text_mode: true,
                editor,
                ..
            }) => editor.insert_str(text),
            Some(PendingInteraction::SessionPicker(picker)) => picker.handle_paste(text),
            Some(_) => {}
            None => {
                self.editor.insert_str(text);
                self.command_selection = 0;
            }
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

        let slash_matches = self.matching_slash_commands();
        if !slash_matches.is_empty() && ctrl {
            match key.code {
                KeyCode::Char('p') => {
                    self.command_selection = self.command_selection.saturating_sub(1);
                    return Action::None;
                }
                KeyCode::Char('n') => {
                    self.command_selection =
                        (self.command_selection + 1).min(slash_matches.len() - 1);
                    return Action::None;
                }
                _ => {}
            }
        }
        if !slash_matches.is_empty() && !ctrl && !alt {
            match key.code {
                KeyCode::Up => {
                    self.command_selection = self.command_selection.saturating_sub(1);
                    return Action::None;
                }
                KeyCode::Down => {
                    self.command_selection =
                        (self.command_selection + 1).min(slash_matches.len() - 1);
                    return Action::None;
                }
                KeyCode::Tab => {
                    let selected =
                        slash_matches[self.command_selection.min(slash_matches.len() - 1)];
                    self.editor.set_text(if selected.takes_argument {
                        format!("{} ", selected.name)
                    } else {
                        selected.name.to_owned()
                    });
                    self.command_selection = 0;
                    return Action::None;
                }
                KeyCode::Enter => {
                    let selected =
                        slash_matches[self.command_selection.min(slash_matches.len() - 1)];
                    self.editor.set_text(selected.name);
                    self.command_selection = 0;
                    return self.submit_editor();
                }
                _ => {}
            }
        }

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
            KeyCode::Char('d') if ctrl => {
                self.editor.delete();
                Action::None
            }
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
                self.editor.delete_word_left();
                Action::None
            }
            KeyCode::Char('k') if ctrl => {
                self.editor.delete_to_line_end();
                Action::None
            }
            KeyCode::Char('u') if ctrl => {
                self.editor.delete_to_line_start();
                Action::None
            }
            KeyCode::Char('y') if ctrl => {
                self.editor.yank();
                Action::None
            }
            KeyCode::Char('j') if ctrl => {
                self.editor.newline();
                Action::None
            }
            KeyCode::Char('b') if alt => {
                self.editor.move_word_left();
                Action::None
            }
            KeyCode::Char('f') if alt => {
                self.editor.move_word_right();
                Action::None
            }
            KeyCode::Enter if alt || shift => {
                self.editor.newline();
                Action::None
            }
            KeyCode::Enter => self.submit_editor(),
            KeyCode::Esc if self.busy => Action::Interrupt,
            KeyCode::Backspace if ctrl => {
                self.editor.delete_word_left();
                self.command_selection = 0;
                Action::None
            }
            KeyCode::Backspace => {
                self.editor.backspace();
                self.command_selection = 0;
                Action::None
            }
            KeyCode::Delete => {
                self.editor.delete();
                self.command_selection = 0;
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
                self.command_selection = 0;
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
                self.turn_started_at = None;
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
        self.show_welcome = false;
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
                    "/model [MODEL] [EFFORT]  모델과 effort 선택\n/effort [LEVEL|auto]  추론 수준\n/resume [SESSION]  이전 세션 선택\n/continue  /resume 별칭\n/new  새 대화\n/status  현재 설정\n/clear  화면 정리\n/quit  종료\n\nEsc 또는 Ctrl+C  실행 중단\nAlt+Enter  줄바꿈",
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
            "/effort" if parts.len() == 1 => {
                let effort_index = if self.effort_is_auto {
                    0
                } else {
                    self.selected_model()
                        .and_then(|model| {
                            model
                                .efforts
                                .iter()
                                .position(|effort| effort.id == self.selected_effort)
                        })
                        .map(|index| index + 1)
                        .unwrap_or(0)
                };
                self.pending = Some(PendingInteraction::EffortPicker { effort_index });
                Action::None
            }
            "/effort" if parts.len() == 2 => {
                let effort = parts[1];
                if effort.eq_ignore_ascii_case("auto") {
                    self.apply_effort(None);
                } else if self
                    .selected_model()
                    .is_some_and(|model| model.supports_effort(effort))
                {
                    self.apply_effort(Some(effort));
                } else {
                    self.committed.push(Block::new(
                        BlockKind::Error,
                        "지원하지 않는 reasoning effort",
                        effort,
                    ));
                }
                Action::None
            }
            "/resume" | "/continue" if self.busy => {
                self.committed.push(Block::new(
                    BlockKind::Warning,
                    "진행 중",
                    "현재 응답을 중단한 뒤 세션을 전환하세요.",
                ));
                Action::None
            }
            "/resume" if parts.len() == 1 => Action::OpenResume,
            "/resume" => Action::ResumeThread(parts[1..].join(" ")),
            "/continue" => Action::OpenResume,
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
                let model = self.selected_model_display_name();
                self.committed.push(Block::new(
                    BlockKind::System,
                    "Status",
                    format!(
                        "thread: {}\nmodel: {model}\neffort: {}\ncwd: {}",
                        self.thread_id,
                        self.effort_label(),
                        self.cwd
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
        let ctrl = key.modifiers.contains(KeyModifiers::CONTROL);
        let alt = key.modifiers.contains(KeyModifiers::ALT);
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
                        effort_index = self.effort_index_for_model(model_index);
                    }
                    KeyCode::Char('k') if !ctrl && !alt => {
                        model_index = model_index.saturating_sub(1);
                        effort_index = self.effort_index_for_model(model_index);
                    }
                    KeyCode::Char('p') if ctrl => {
                        model_index = model_index.saturating_sub(1);
                        effort_index = self.effort_index_for_model(model_index);
                    }
                    KeyCode::Down => {
                        model_index = (model_index + 1).min(self.models.len().saturating_sub(1));
                        effort_index = self.effort_index_for_model(model_index);
                    }
                    KeyCode::Char('j') if !ctrl && !alt => {
                        model_index = (model_index + 1).min(self.models.len().saturating_sub(1));
                        effort_index = self.effort_index_for_model(model_index);
                    }
                    KeyCode::Char('n') if ctrl => {
                        model_index = (model_index + 1).min(self.models.len().saturating_sub(1));
                        effort_index = self.effort_index_for_model(model_index);
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
                        effort_index = (effort_index + 1).min(count - 1);
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
            PendingInteraction::EffortPicker { mut effort_index } => {
                let count = self
                    .selected_model()
                    .map(|model| model.efforts.len() + 1)
                    .unwrap_or(1);
                match key.code {
                    KeyCode::Esc => return Action::None,
                    KeyCode::Left | KeyCode::Up => {
                        effort_index = effort_index.saturating_sub(1);
                    }
                    KeyCode::Char('p') if ctrl => {
                        effort_index = effort_index.saturating_sub(1);
                    }
                    KeyCode::Right | KeyCode::Down | KeyCode::Tab => {
                        effort_index = (effort_index + 1).min(count - 1);
                    }
                    KeyCode::Char('n') if ctrl => {
                        effort_index = (effort_index + 1).min(count - 1);
                    }
                    KeyCode::Enter => {
                        let effort = effort_index.checked_sub(1).and_then(|index| {
                            self.selected_model()
                                .and_then(|model| model.efforts.get(index))
                                .map(|effort| effort.id.clone())
                        });
                        self.apply_effort(effort.as_deref());
                        return Action::None;
                    }
                    _ => {}
                }
                self.pending = Some(PendingInteraction::EffortPicker { effort_index });
                Action::None
            }
            PendingInteraction::SessionPicker(mut picker) => match picker.handle_key(key) {
                SessionPickerResult::None => {
                    self.pending = Some(PendingInteraction::SessionPicker(picker));
                    Action::None
                }
                SessionPickerResult::Cancel => Action::None,
                SessionPickerResult::Select(thread_id) => Action::ResumeThread(thread_id),
            },
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
                        KeyCode::Backspace if ctrl => editor.delete_word_left(),
                        KeyCode::Backspace => editor.backspace(),
                        KeyCode::Delete => editor.delete(),
                        KeyCode::Left if ctrl || alt => editor.move_word_left(),
                        KeyCode::Right if ctrl || alt => editor.move_word_right(),
                        KeyCode::Left => editor.move_left(),
                        KeyCode::Right => editor.move_right(),
                        KeyCode::Char('w') if ctrl => editor.delete_word_left(),
                        KeyCode::Char('k') if ctrl => editor.delete_to_line_end(),
                        KeyCode::Char('u') if ctrl => editor.delete_to_line_start(),
                        KeyCode::Char('y') if ctrl => editor.yank(),
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
                let mut lines = self.models[start..end]
                    .iter()
                    .enumerate()
                    .map(|(offset, model)| {
                        let index = start + offset;
                        OverlayLine {
                            text: model.display_name.clone(),
                            selected: index == *model_index,
                            muted: false,
                        }
                    })
                    .collect::<Vec<_>>();
                if let Some(model) = self.models.get(*model_index) {
                    lines.push(OverlayLine {
                        text: String::new(),
                        selected: false,
                        muted: true,
                    });
                    lines.push(OverlayLine {
                        text: format!("Effort  {}", effort_slider(model, *effort_index, false)),
                        selected: false,
                        muted: false,
                    });
                }
                Some(OverlayView {
                    title: "Select model".to_owned(),
                    lines,
                    hint: "↑/↓ select · ←/→ effort · Enter confirm · Esc cancel".to_owned(),
                    input: None,
                    input_label: "",
                    input_placeholder: "",
                })
            }
            PendingInteraction::EffortPicker { effort_index } => {
                let model = self.selected_model()?;
                Some(OverlayView {
                    title: format!("Set effort · {}", model.display_name),
                    lines: vec![OverlayLine {
                        text: effort_slider(model, *effort_index, true),
                        selected: false,
                        muted: false,
                    }],
                    hint: "←/→ adjust · Enter confirm · Esc cancel".to_owned(),
                    input: None,
                    input_label: "",
                    input_placeholder: "",
                })
            }
            PendingInteraction::SessionPicker(picker) => Some(picker.overlay_view()),
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
                    input_label: "",
                    input_placeholder: "",
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
                    input_label: "Answer",
                    input_placeholder: "Type your answer…",
                })
            }
        }
    }

    fn matching_slash_commands(&self) -> Vec<&'static SlashCommand> {
        let text = self.editor.text();
        if !text.starts_with('/') || text.chars().any(char::is_whitespace) {
            return Vec::new();
        }
        SLASH_COMMANDS
            .iter()
            .filter(|command| command.name.starts_with(&text))
            .collect()
    }

    fn slash_suggestion_views(&self) -> Vec<SuggestionView> {
        self.matching_slash_commands()
            .into_iter()
            .enumerate()
            .map(|(index, command)| SuggestionView {
                command: command.name.to_owned(),
                description: command.description.to_owned(),
                selected: index == self.command_selection,
            })
            .collect()
    }

    fn activity(&self) -> Option<String> {
        if !self.busy {
            return None;
        }
        let elapsed = self
            .turn_started_at
            .map(|started| started.elapsed().as_secs())
            .unwrap_or(0);
        Some(format!(
            "{} Working… {}s · Esc to interrupt",
            SPINNER[self.spinner_frame], elapsed
        ))
    }

    fn status_line(&self) -> StatusLineView {
        let context = self.context_window.and_then(|window| {
            (window > 0).then(|| {
                format!(
                    "ctx: {}/{} ({}%)",
                    format_token_count(self.total_tokens),
                    format_token_count(window),
                    self.total_tokens.saturating_mul(100) / window
                )
            })
        });
        StatusLineView {
            model: self.selected_model_display_name().to_owned(),
            effort: self.selected_effort.clone(),
            context,
            notice: self.transient_status.clone(),
        }
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
        self.effort_is_auto = false;
        self.committed.push(Block::new(
            BlockKind::Success,
            "Model 변경",
            format!("{model_name} · {selected_effort}"),
        ));
    }

    fn apply_effort(&mut self, effort: Option<&str>) {
        let Some(model) = self.selected_model() else {
            return;
        };
        let selected = effort
            .filter(|effort| model.supports_effort(effort))
            .unwrap_or(&model.default_effort)
            .to_owned();
        let label = match effort {
            Some(_) => selected.clone(),
            None => format!("auto · {selected}"),
        };
        self.selected_effort = selected;
        self.effort_is_auto = effort.is_none();
        self.committed
            .push(Block::new(BlockKind::Success, "Effort changed", label));
    }

    fn effort_label(&self) -> String {
        if self.effort_is_auto {
            format!("auto · {}", self.selected_effort)
        } else {
            self.selected_effort.clone()
        }
    }

    fn effort_index_for_model(&self, model_index: usize) -> usize {
        let Some(model) = self.models.get(model_index) else {
            return 0;
        };
        model
            .efforts
            .iter()
            .position(|effort| effort.id == self.selected_effort)
            .or_else(|| {
                model
                    .efforts
                    .iter()
                    .position(|effort| effort.id == model.default_effort)
            })
            .unwrap_or(0)
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
            let duration = item
                .get("durationMs")
                .and_then(Value::as_u64)
                .map(format_duration)
                .map(|duration| format!(" · {duration}"))
                .unwrap_or_default();
            Some(Block::new(
                if status == "completed" {
                    BlockKind::Tool
                } else {
                    BlockKind::Warning
                },
                format!(
                    "Bash · {}{suffix}{duration}",
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
            let diff = change
                .get("diff")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let additions = diff
                .lines()
                .filter(|line| line.starts_with('+') && !line.starts_with("+++"))
                .count();
            let deletions = diff
                .lines()
                .filter(|line| line.starts_with('-') && !line.starts_with("---"))
                .count();
            let stats = match (additions, deletions) {
                (0, 0) => String::new(),
                _ => format!("  +{additions} -{deletions}"),
            };
            let marker = match kind {
                "add" => "+",
                "delete" => "-",
                _ => "±",
            };
            format!("{marker}  {path}{stats}")
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn format_duration(duration_ms: u64) -> String {
    if duration_ms < 1_000 {
        format!("{duration_ms}ms")
    } else {
        format!("{:.1}s", duration_ms as f64 / 1_000.0)
    }
}

fn effort_slider(model: &ModelInfo, selected: usize, include_auto: bool) -> String {
    let mut levels = Vec::with_capacity(model.efforts.len() + usize::from(include_auto));
    if include_auto {
        levels.push("auto".to_owned());
    }
    levels.extend(model.efforts.iter().map(|effort| effort.id.clone()));
    levels
        .into_iter()
        .enumerate()
        .map(|(index, level)| {
            if index == selected {
                format!("[{level}]")
            } else {
                level
            }
        })
        .collect::<Vec<_>>()
        .join(" ─ ")
}

fn relative_time(timestamp: u64) -> String {
    if timestamp == 0 {
        return "unknown".to_owned();
    }
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or(timestamp);
    let elapsed = now.saturating_sub(timestamp);
    match elapsed {
        0..=59 => "now".to_owned(),
        60..=3_599 => format!("{}m ago", elapsed / 60),
        3_600..=86_399 => format!("{}h ago", elapsed / 3_600),
        86_400..=604_799 => format!("{}d ago", elapsed / 86_400),
        _ => format!("{}w ago", elapsed / 604_800),
    }
}

fn path_eq(left: &str, right: &str) -> bool {
    #[cfg(windows)]
    {
        left.eq_ignore_ascii_case(right)
    }
    #[cfg(not(windows))]
    {
        left == right
    }
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

fn format_token_count(tokens: u64) -> String {
    if tokens >= 1_000 {
        format!("{}k", tokens.saturating_add(500) / 1_000)
    } else {
        tokens.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_picker_scopes_to_cwd_and_can_expand_to_all_projects() {
        let sessions = vec![
            SessionInfo {
                id: "current".to_owned(),
                name: Some("Current project".to_owned()),
                preview: String::new(),
                cwd: r"C:\work\current".to_owned(),
                updated_at: 2,
            },
            SessionInfo {
                id: "other".to_owned(),
                name: Some("Other project".to_owned()),
                preview: String::new(),
                cwd: r"C:\work\other".to_owned(),
                updated_at: 1,
            },
        ];
        let mut picker = SessionPicker::new(sessions, r"C:\work\current".to_owned(), None);

        assert_eq!(picker.filtered_len(), 1);
        picker.handle_key(KeyEvent::new(KeyCode::Char('a'), KeyModifiers::CONTROL));
        assert_eq!(picker.filtered_len(), 2);
    }

    #[test]
    fn effort_auto_remains_selected_until_an_explicit_level_is_chosen() {
        let model = ModelInfo {
            id: "model".to_owned(),
            model: "model".to_owned(),
            display_name: "Model".to_owned(),
            efforts: vec![
                EffortInfo {
                    id: "high".to_owned(),
                },
                EffortInfo {
                    id: "max".to_owned(),
                },
            ],
            default_effort: "high".to_owned(),
            is_default: true,
        };
        let mut state = AppState::new(
            "thread".to_owned(),
            "cwd".to_owned(),
            "account".to_owned(),
            vec![model],
            "model",
            Some("high"),
        );

        state.run_slash_command("/effort auto");
        assert!(state.effort_is_auto);
        assert_eq!(state.effort_label(), "auto · high");

        state.run_slash_command("/effort max");
        assert!(!state.effort_is_auto);
        assert_eq!(state.selected_effort(), "max");
    }
}
