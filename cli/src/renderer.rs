use std::io::{Stdout, Write, stdout};

use anyhow::Result;
use crossterm::{
    cursor::{Hide, MoveDown, MoveTo, MoveToColumn, MoveUp, Show},
    event::{DisableBracketedPaste, EnableBracketedPaste},
    execute, queue,
    style::{Attribute, Color, Print, ResetColor, SetAttribute, SetForegroundColor},
    terminal::{Clear, ClearType, disable_raw_mode, enable_raw_mode, size as terminal_size},
};
use unicode_width::{UnicodeWidthChar, UnicodeWidthStr};

use crate::editor::Editor;

#[derive(Clone, Copy)]
pub enum BlockKind {
    User,
    Assistant,
    Reasoning,
    Tool,
    Success,
    Warning,
    Error,
    System,
}

#[derive(Clone)]
pub struct Block {
    pub kind: BlockKind,
    pub title: String,
    pub body: String,
}

impl Block {
    pub fn new(kind: BlockKind, title: impl Into<String>, body: impl Into<String>) -> Self {
        Self {
            kind,
            title: title.into(),
            body: body.into(),
        }
    }
}

pub struct OverlayView<'a> {
    pub title: String,
    pub lines: Vec<OverlayLine>,
    pub hint: String,
    pub input: Option<&'a Editor>,
}

pub struct OverlayLine {
    pub text: String,
    pub selected: bool,
    pub muted: bool,
}

pub struct WelcomeView {
    pub model: String,
    pub effort: String,
    pub cwd: String,
    pub account: String,
}

pub struct SuggestionView {
    pub command: String,
    pub description: String,
    pub selected: bool,
}

pub struct View<'a> {
    pub live_blocks: Vec<Block>,
    pub overlay: Option<OverlayView<'a>>,
    pub editor: &'a Editor,
    pub welcome: Option<WelcomeView>,
    pub suggestions: Vec<SuggestionView>,
    pub activity: Option<String>,
    pub footer: String,
}

pub struct TerminalSession;

impl TerminalSession {
    pub fn enter() -> Result<Self> {
        enable_raw_mode()?;
        execute!(stdout(), EnableBracketedPaste, Show)?;
        Ok(Self)
    }
}

impl Drop for TerminalSession {
    fn drop(&mut self) {
        let _ = execute!(
            stdout(),
            ResetColor,
            SetAttribute(Attribute::Reset),
            Show,
            DisableBracketedPaste
        );
        let _ = disable_raw_mode();
    }
}

pub struct Renderer {
    out: Stdout,
    live_rows: u16,
    cursor_from_bottom: u16,
    last_width: u16,
}

impl Renderer {
    pub fn new() -> Self {
        Self {
            out: stdout(),
            live_rows: 0,
            cursor_from_bottom: 0,
            last_width: 0,
        }
    }

    pub fn clear_screen(&mut self) -> Result<()> {
        self.live_rows = 0;
        self.cursor_from_bottom = 0;
        execute!(self.out, Clear(ClearType::All), MoveTo(0, 0), Show)?;
        Ok(())
    }

    pub fn render(&mut self, committed: &[Block], view: View<'_>) -> Result<()> {
        self.erase_live()?;
        let (width, height) = terminal_size().unwrap_or((100, 30));
        self.last_width = width;

        for block in committed {
            let lines = block_lines(block, width.max(20));
            self.print_permanent(&lines)?;
        }

        let mut frame = if let Some(overlay) = view.overlay {
            overlay_frame(&view.live_blocks, overlay, &view.footer, width.max(20))
        } else {
            normal_frame(
                &view.live_blocks,
                view.editor,
                view.welcome,
                &view.suggestions,
                view.activity.as_deref(),
                &view.footer,
                width.max(20),
            )
        };

        let max_live = height.saturating_sub(1).max(3) as usize;
        if frame.lines.len() > max_live {
            let dropped = frame.lines.len() - max_live;
            frame.lines.drain(0..dropped);
            frame.cursor_line = frame.cursor_line.saturating_sub(dropped);
        }

        self.print_frame(&frame)?;
        self.live_rows = frame.lines.len() as u16;
        self.cursor_from_bottom = (frame.lines.len() - 1 - frame.cursor_line) as u16;
        self.out.flush()?;
        Ok(())
    }

    pub fn finish(&mut self) -> Result<()> {
        self.erase_live()?;
        queue!(self.out, Show, ResetColor, Print("\r\n"))?;
        self.out.flush()?;
        Ok(())
    }

    fn erase_live(&mut self) -> Result<()> {
        if self.live_rows == 0 {
            return Ok(());
        }

        if self.cursor_from_bottom > 0 {
            queue!(self.out, MoveDown(self.cursor_from_bottom))?;
        }
        queue!(self.out, MoveToColumn(0))?;
        if self.live_rows > 1 {
            queue!(self.out, MoveUp(self.live_rows - 1))?;
        }
        queue!(self.out, Clear(ClearType::FromCursorDown))?;
        self.live_rows = 0;
        self.cursor_from_bottom = 0;
        Ok(())
    }

    fn print_permanent(&mut self, lines: &[PaintLine]) -> Result<()> {
        for line in lines {
            print_line(&mut self.out, line)?;
            queue!(self.out, Print("\r\n"))?;
        }
        self.out.flush()?;
        Ok(())
    }

    fn print_frame(&mut self, frame: &Frame) -> Result<()> {
        queue!(self.out, Hide)?;
        for (index, line) in frame.lines.iter().enumerate() {
            print_line(&mut self.out, line)?;
            if index + 1 < frame.lines.len() {
                queue!(self.out, Print("\r\n"))?;
            }
        }

        let bottom_distance = (frame.lines.len() - 1 - frame.cursor_line) as u16;
        if bottom_distance > 0 {
            queue!(self.out, MoveUp(bottom_distance))?;
        }
        queue!(
            self.out,
            MoveToColumn(frame.cursor_col.min(u16::MAX as usize) as u16)
        )?;
        if frame.show_cursor {
            queue!(self.out, Show)?;
        }
        Ok(())
    }
}

struct Frame {
    lines: Vec<PaintLine>,
    cursor_line: usize,
    cursor_col: usize,
    show_cursor: bool,
}

#[derive(Clone, Copy)]
enum Tone {
    Plain,
    Muted,
    Accent,
    User,
    Success,
    Warning,
    Error,
    Code,
}

struct PaintLine {
    prefix: String,
    prefix_tone: Tone,
    text: String,
    tone: Tone,
    bold: bool,
}

impl PaintLine {
    fn plain(text: impl Into<String>) -> Self {
        Self {
            prefix: String::new(),
            prefix_tone: Tone::Plain,
            text: text.into(),
            tone: Tone::Plain,
            bold: false,
        }
    }

    fn blank() -> Self {
        Self::plain("")
    }
}

fn normal_frame(
    live: &[Block],
    editor: &Editor,
    welcome: Option<WelcomeView>,
    suggestions: &[SuggestionView],
    activity: Option<&str>,
    footer: &str,
    width: u16,
) -> Frame {
    let mut lines = Vec::new();
    if let Some(welcome) = welcome {
        lines.extend(welcome_lines(welcome, width));
        lines.push(PaintLine::blank());
    }

    for block in live {
        lines.extend(block_lines(block, width));
    }
    if let Some(activity) = activity {
        lines.extend(wrapped_line(
            "",
            Tone::Accent,
            activity,
            Tone::Accent,
            false,
            width,
        ));
        lines.push(PaintLine::blank());
    } else if !live.is_empty() {
        lines.push(PaintLine::blank());
    }

    if !suggestions.is_empty() {
        lines.extend(suggestion_lines(suggestions, width));
        lines.push(PaintLine::blank());
    }

    let (input_lines, input_cursor_line, input_cursor_col) = input_lines(editor, width);
    let cursor_line = lines.len() + input_cursor_line;
    lines.extend(input_lines);
    lines.push(PaintLine {
        prefix: "  ".to_owned(),
        prefix_tone: Tone::Muted,
        text: footer.to_owned(),
        tone: Tone::Muted,
        bold: false,
    });

    Frame {
        lines,
        cursor_line,
        cursor_col: input_cursor_col,
        show_cursor: true,
    }
}

fn welcome_lines(welcome: WelcomeView, width: u16) -> Vec<PaintLine> {
    let panel_width = (width as usize).clamp(34, 76);
    let inner_width = panel_width.saturating_sub(2);
    let mut lines = Vec::new();
    lines.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Accent,
        text: format!("╭{}╮", "─".repeat(inner_width)),
        tone: Tone::Accent,
        bold: false,
    });
    lines.push(panel_line(
        "  ✦  DEVEZ CODE",
        panel_width,
        Tone::Accent,
        true,
    ));
    lines.push(panel_line(
        "     Codex, with a calmer terminal",
        panel_width,
        Tone::Muted,
        false,
    ));
    lines.push(panel_line("", panel_width, Tone::Plain, false));
    lines.push(panel_line(
        &format!("  Model    {} · {}", welcome.model, welcome.effort),
        panel_width,
        Tone::Plain,
        false,
    ));
    lines.push(panel_line(
        &format!("  Account  {}", welcome.account),
        panel_width,
        Tone::Plain,
        false,
    ));
    lines.push(panel_line(
        &format!(
            "  Folder   {}",
            compact_text(&welcome.cwd, inner_width.saturating_sub(11))
        ),
        panel_width,
        Tone::Plain,
        false,
    ));
    lines.push(panel_line("", panel_width, Tone::Plain, false));
    lines.push(panel_line(
        "  /help commands  ·  /model switch model",
        panel_width,
        Tone::Muted,
        false,
    ));
    lines.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!("╰{}╯", "─".repeat(inner_width)),
        tone: Tone::Muted,
        bold: false,
    });
    lines
}

fn suggestion_lines(suggestions: &[SuggestionView], width: u16) -> Vec<PaintLine> {
    let panel_width = (width as usize).clamp(34, 76);
    let inner_width = panel_width.saturating_sub(2);
    let mut lines = vec![PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!("╭─ Commands {}", "─".repeat(inner_width.saturating_sub(11))),
        tone: Tone::Muted,
        bold: false,
    }];
    for suggestion in suggestions.iter().take(6) {
        let marker = if suggestion.selected { "❯" } else { " " };
        let content = format!(
            " {marker} {:<10} {}",
            suggestion.command, suggestion.description
        );
        lines.push(panel_line(
            &content,
            panel_width,
            if suggestion.selected {
                Tone::Accent
            } else {
                Tone::Muted
            },
            suggestion.selected,
        ));
    }
    lines.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!("╰{}╯", "─".repeat(inner_width)),
        tone: Tone::Muted,
        bold: false,
    });
    lines
}

fn panel_line(text: &str, width: usize, tone: Tone, bold: bool) -> PaintLine {
    let inner_width = width.saturating_sub(2);
    let content = compact_text(text, inner_width);
    let padding = inner_width.saturating_sub(UnicodeWidthStr::width(content.as_str()));
    PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!("│{content}{}│", " ".repeat(padding)),
        tone,
        bold,
    }
}

fn overlay_frame(live: &[Block], overlay: OverlayView<'_>, footer: &str, width: u16) -> Frame {
    let mut lines = Vec::new();
    for block in live {
        lines.extend(block_lines(block, width));
    }
    if !live.is_empty() {
        lines.push(PaintLine::blank());
    }

    let title_width = UnicodeWidthStr::width(overlay.title.as_str());
    lines.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Accent,
        text: format!(
            "╭─ {} {}",
            overlay.title,
            "─".repeat(
                (width as usize)
                    .saturating_sub(title_width)
                    .saturating_sub(5)
            )
        ),
        tone: Tone::Accent,
        bold: true,
    });
    for row in overlay.lines {
        for (part_index, part) in row.text.lines().enumerate() {
            let prefix = if part_index == 0 {
                if row.selected { "│ ❯ " } else { "│   " }
            } else {
                "│     "
            };
            lines.extend(wrapped_line(
                prefix,
                if row.selected {
                    Tone::Accent
                } else {
                    Tone::Muted
                },
                part,
                if row.muted { Tone::Muted } else { Tone::Plain },
                row.selected && part_index == 0,
                width,
            ));
        }
    }
    lines.push(PaintLine {
        prefix: "╰─ ".to_owned(),
        prefix_tone: Tone::Muted,
        text: overlay.hint,
        tone: Tone::Muted,
        bold: false,
    });
    let mut cursor_line = lines.len() - 1;
    let mut cursor_col = 0;
    let show_cursor = if let Some(editor) = overlay.input {
        let (input, input_cursor_line, input_cursor_col) = input_lines(editor, width);
        cursor_line = lines.len() + input_cursor_line;
        cursor_col = input_cursor_col;
        lines.extend(input);
        true
    } else {
        false
    };
    lines.push(PaintLine {
        prefix: "  ".to_owned(),
        prefix_tone: Tone::Muted,
        text: footer.to_owned(),
        tone: Tone::Muted,
        bold: false,
    });

    Frame {
        cursor_line,
        cursor_col,
        lines,
        show_cursor,
    }
}

fn block_lines(block: &Block, width: u16) -> Vec<PaintLine> {
    let (marker, tone) = match block.kind {
        BlockKind::User => ("❯ ", Tone::User),
        BlockKind::Assistant => ("● ", Tone::Accent),
        BlockKind::Reasoning => ("✻ ", Tone::Muted),
        BlockKind::Tool => ("● ", Tone::User),
        BlockKind::Success => ("✓ ", Tone::Success),
        BlockKind::Warning => ("▲ ", Tone::Warning),
        BlockKind::Error => ("✕ ", Tone::Error),
        BlockKind::System => ("◆ ", Tone::Muted),
    };

    let mut lines = wrapped_line(marker, tone, &block.title, Tone::Plain, true, width);
    if block.body.is_empty() {
        return lines;
    }

    let mut code = false;
    for raw_line in block.body.lines() {
        let trimmed = raw_line.trim_start();
        if let Some(language) = trimmed.strip_prefix("```") {
            if code {
                lines.push(PaintLine {
                    prefix: "  ".to_owned(),
                    prefix_tone: Tone::Muted,
                    text: "└────────────────".to_owned(),
                    tone: Tone::Muted,
                    bold: false,
                });
            } else {
                let label = if language.trim().is_empty() {
                    "code"
                } else {
                    language.trim()
                };
                lines.push(PaintLine {
                    prefix: "  ".to_owned(),
                    prefix_tone: Tone::Muted,
                    text: format!("┌─ {label}"),
                    tone: Tone::Muted,
                    bold: false,
                });
            }
            code = !code;
            continue;
        }

        if code {
            lines.extend(wrapped_line(
                "  │ ",
                Tone::Muted,
                raw_line,
                Tone::Code,
                false,
                width,
            ));
        } else if trimmed.starts_with('#') {
            lines.extend(wrapped_line(
                "  ",
                Tone::Muted,
                trimmed.trim_start_matches('#').trim_start(),
                Tone::Plain,
                true,
                width,
            ));
        } else if let Some(item) = trimmed
            .strip_prefix("- ")
            .or_else(|| trimmed.strip_prefix("* "))
        {
            lines.extend(wrapped_line(
                "  • ",
                Tone::Accent,
                item,
                Tone::Plain,
                false,
                width,
            ));
        } else if let Some(quote) = trimmed.strip_prefix("> ") {
            lines.extend(wrapped_line(
                "  │ ",
                Tone::Muted,
                quote,
                Tone::Muted,
                false,
                width,
            ));
        } else {
            lines.extend(wrapped_line(
                "  ",
                Tone::Muted,
                raw_line,
                Tone::Plain,
                false,
                width,
            ));
        }
    }
    if code {
        lines.push(PaintLine {
            prefix: "  ".to_owned(),
            prefix_tone: Tone::Muted,
            text: "└────────────────".to_owned(),
            tone: Tone::Muted,
            bold: false,
        });
    }
    lines.push(PaintLine::blank());
    lines
}

fn wrapped_line(
    prefix: &str,
    prefix_tone: Tone,
    text: &str,
    tone: Tone,
    bold: bool,
    width: u16,
) -> Vec<PaintLine> {
    let width = width as usize;
    let prefix_width = UnicodeWidthStr::width(prefix);
    let available = width.saturating_sub(prefix_width).max(4);
    let options = textwrap::Options::new(available)
        .break_words(true)
        .word_separator(textwrap::WordSeparator::UnicodeBreakProperties);
    let wrapped = textwrap::wrap(text, options);
    if wrapped.is_empty() {
        return vec![PaintLine {
            prefix: prefix.to_owned(),
            prefix_tone,
            text: String::new(),
            tone,
            bold,
        }];
    }

    wrapped
        .into_iter()
        .enumerate()
        .map(|(index, part)| PaintLine {
            prefix: if index == 0 {
                prefix.to_owned()
            } else {
                " ".repeat(prefix_width)
            },
            prefix_tone,
            text: part.into_owned(),
            tone,
            bold,
        })
        .collect()
}

fn input_lines(editor: &Editor, width: u16) -> (Vec<PaintLine>, usize, usize) {
    let panel_width = (width as usize).saturating_sub(1).max(16);
    let first_prefix = "│ ❯ ";
    let continuation_prefix = "│   ";
    let content_width = panel_width
        .saturating_sub(UnicodeWidthStr::width(first_prefix))
        .saturating_sub(1)
        .max(4);
    let mut raw_rows = vec![String::new()];
    let mut row = 0;
    let mut column = UnicodeWidthStr::width(first_prefix);
    let mut cursor_row = 0;
    let mut cursor_column = column;

    for (index, ch) in editor.chars().iter().copied().enumerate() {
        if index == editor.cursor() {
            cursor_row = row;
            cursor_column = column;
        }

        if ch == '\n' {
            raw_rows.push(String::new());
            row += 1;
            column = UnicodeWidthStr::width(continuation_prefix);
            continue;
        }

        let ch_width = UnicodeWidthChar::width(ch).unwrap_or(0);
        let content_column = column.saturating_sub(UnicodeWidthStr::width(if row == 0 {
            first_prefix
        } else {
            continuation_prefix
        }));
        if content_column + ch_width > content_width && !raw_rows[row].is_empty() {
            raw_rows.push(String::new());
            row += 1;
            column = UnicodeWidthStr::width(continuation_prefix);
            if index == editor.cursor() {
                cursor_row = row;
                cursor_column = column;
            }
        }
        raw_rows[row].push(ch);
        column += ch_width;
    }

    if editor.cursor() == editor.chars().len() {
        cursor_row = row;
        cursor_column = column;
    }

    let mut rows = Vec::with_capacity(raw_rows.len() + 2);
    let top_label = " Message ";
    rows.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!(
            "╭─{top_label}{}╮",
            "─".repeat(panel_width.saturating_sub(3 + top_label.len()))
        ),
        tone: Tone::Muted,
        bold: false,
    });
    for (index, raw) in raw_rows.into_iter().enumerate() {
        let is_placeholder = editor.is_empty() && index == 0;
        let content = if is_placeholder {
            "Ask Codex to build, fix, or explain…".to_owned()
        } else {
            raw
        };
        let padding = content_width.saturating_sub(UnicodeWidthStr::width(content.as_str()));
        rows.push(PaintLine {
            prefix: if index == 0 {
                first_prefix.to_owned()
            } else {
                continuation_prefix.to_owned()
            },
            prefix_tone: if index == 0 {
                Tone::Accent
            } else {
                Tone::Muted
            },
            text: format!("{content}{}│", " ".repeat(padding)),
            tone: if is_placeholder {
                Tone::Muted
            } else {
                Tone::Plain
            },
            bold: false,
        });
    }
    rows.push(PaintLine {
        prefix: String::new(),
        prefix_tone: Tone::Muted,
        text: format!("╰{}╯", "─".repeat(panel_width.saturating_sub(2))),
        tone: Tone::Muted,
        bold: false,
    });

    (rows, cursor_row + 1, cursor_column)
}

fn compact_text(text: &str, max_width: usize) -> String {
    if UnicodeWidthStr::width(text) <= max_width {
        return text.to_owned();
    }
    if max_width <= 1 {
        return "…".to_owned();
    }
    let mut output = String::new();
    let mut width = 0;
    for ch in text.chars().rev() {
        let ch_width = UnicodeWidthChar::width(ch).unwrap_or(0);
        if width + ch_width >= max_width {
            break;
        }
        output.insert(0, ch);
        width += ch_width;
    }
    format!("…{output}")
}

fn print_line(out: &mut Stdout, line: &PaintLine) -> Result<()> {
    set_tone(out, line.prefix_tone)?;
    queue!(out, Print(&line.prefix))?;
    set_tone(out, line.tone)?;
    if line.bold {
        queue!(out, SetAttribute(Attribute::Bold))?;
    }
    queue!(
        out,
        Print(&line.text),
        SetAttribute(Attribute::Reset),
        ResetColor
    )?;
    Ok(())
}

fn set_tone(out: &mut Stdout, tone: Tone) -> Result<()> {
    let color = match tone {
        Tone::Plain => Color::Reset,
        Tone::Muted => Color::Rgb {
            r: 128,
            g: 128,
            b: 128,
        },
        Tone::Accent => Color::Rgb {
            r: 216,
            g: 142,
            b: 93,
        },
        Tone::User => Color::Rgb {
            r: 104,
            g: 171,
            b: 255,
        },
        Tone::Success => Color::Rgb {
            r: 91,
            g: 192,
            b: 134,
        },
        Tone::Warning => Color::Rgb {
            r: 232,
            g: 184,
            b: 73,
        },
        Tone::Error => Color::Rgb {
            r: 238,
            g: 99,
            b: 99,
        },
        Tone::Code => Color::Rgb {
            r: 183,
            g: 203,
            b: 224,
        },
    };
    queue!(out, SetForegroundColor(color))?;
    Ok(())
}
