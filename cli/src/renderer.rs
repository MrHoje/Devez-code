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

pub struct View<'a> {
    pub live_blocks: Vec<Block>,
    pub overlay: Option<OverlayView<'a>>,
    pub editor: &'a Editor,
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
            normal_frame(&view.live_blocks, view.editor, &view.footer, width.max(20))
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

fn normal_frame(live: &[Block], editor: &Editor, footer: &str, width: u16) -> Frame {
    let mut lines = Vec::new();
    for block in live {
        lines.extend(block_lines(block, width));
    }
    if !live.is_empty() {
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

fn overlay_frame(live: &[Block], overlay: OverlayView<'_>, footer: &str, width: u16) -> Frame {
    let mut lines = Vec::new();
    for block in live {
        lines.extend(block_lines(block, width));
    }
    if !live.is_empty() {
        lines.push(PaintLine::blank());
    }

    lines.push(PaintLine {
        prefix: "◆ ".to_owned(),
        prefix_tone: Tone::Accent,
        text: overlay.title,
        tone: Tone::Plain,
        bold: true,
    });
    for row in overlay.lines {
        let prefix = if row.selected { "  ❯ " } else { "    " };
        lines.extend(wrapped_line(
            prefix,
            if row.selected {
                Tone::Accent
            } else {
                Tone::Muted
            },
            &row.text,
            if row.muted { Tone::Muted } else { Tone::Plain },
            row.selected,
            width,
        ));
    }
    lines.push(PaintLine {
        prefix: "    ".to_owned(),
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
        if raw_line.trim_start().starts_with("```") {
            code = !code;
            continue;
        }
        let trimmed = raw_line.trim_start();
        let (body_tone, bold) = if code {
            (Tone::Code, false)
        } else if trimmed.starts_with('#') {
            (Tone::Plain, true)
        } else {
            (Tone::Plain, false)
        };
        lines.extend(wrapped_line(
            "  ",
            Tone::Muted,
            raw_line,
            body_tone,
            bold,
            width,
        ));
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
    let width = width.max(8) as usize;
    let first_prefix = "❯ ";
    let continuation_prefix = "  ";
    let mut rows = vec![PaintLine {
        prefix: first_prefix.to_owned(),
        prefix_tone: Tone::Accent,
        text: String::new(),
        tone: Tone::Plain,
        bold: false,
    }];
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
            rows.push(PaintLine {
                prefix: continuation_prefix.to_owned(),
                prefix_tone: Tone::Muted,
                text: String::new(),
                tone: Tone::Plain,
                bold: false,
            });
            row += 1;
            column = UnicodeWidthStr::width(continuation_prefix);
            continue;
        }

        let ch_width = UnicodeWidthChar::width(ch).unwrap_or(0);
        if column + ch_width >= width && !rows[row].text.is_empty() {
            rows.push(PaintLine {
                prefix: continuation_prefix.to_owned(),
                prefix_tone: Tone::Muted,
                text: String::new(),
                tone: Tone::Plain,
                bold: false,
            });
            row += 1;
            column = UnicodeWidthStr::width(continuation_prefix);
            if index == editor.cursor() {
                cursor_row = row;
                cursor_column = column;
            }
        }
        rows[row].text.push(ch);
        column += ch_width;
    }

    if editor.cursor() == editor.chars().len() {
        cursor_row = row;
        cursor_column = column;
    }
    (rows, cursor_row, cursor_column)
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
