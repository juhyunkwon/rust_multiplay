use crate::entry::{LogEntry, LogLevel};
use eframe::egui::{self, Color32, RichText, ScrollArea, Sense};
use std::fs::{self, File};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use x0_core::{AppConfig, ConfigPath};

const MAX_LINES: usize = 12_000;
const TAIL_BYTES: u64 = 2 * 1024 * 1024;
const POLL: Duration = Duration::from_millis(250);

pub struct LogViewerApp {
    log_dir: PathBuf,
    files: Vec<PathBuf>,
    selected: Option<PathBuf>,
    entries: Vec<LogEntry>,
    view: Vec<usize>,
    pending: String,
    offset: u64,
    search: String,
    show_error: bool,
    show_warn: bool,
    show_info: bool,
    show_debug: bool,
    show_trace: bool,
    show_packets: bool,
    follow: bool,
    selected_line: Option<usize>,
    status: String,
    last_scan: Instant,
    last_poll: Instant,
    last_filter: String,
}

impl LogViewerApp {
    pub fn new() -> Self {
        let cfg = AppConfig::load();
        let log_dir = ConfigPath::get_log_directory(Some(&cfg.logging.log_path));
        let mut app = Self {
            log_dir,
            files: Vec::new(),
            selected: None,
            entries: Vec::new(),
            view: Vec::new(),
            pending: String::new(),
            offset: 0,
            search: String::new(),
            show_error: true,
            show_warn: true,
            show_info: true,
            show_debug: true,
            show_trace: false,
            show_packets: false,
            follow: true,
            selected_line: None,
            status: String::new(),
            last_scan: Instant::now() - Duration::from_secs(10),
            last_poll: Instant::now() - Duration::from_secs(10),
            last_filter: String::new(),
        };
        app.scan_files();
        if let Some(first) = app.files.first().cloned() {
            app.open_file(first);
        } else {
            app.status = format!(
                "로그 파일이 없습니다. server-host를 실행하면 {} 에 JSON 로그가 생깁니다.",
                app.log_dir.display()
            );
        }
        app
    }

    fn scan_files(&mut self) {
        self.files = list_log_files(&self.log_dir);
        self.last_scan = Instant::now();
    }

    fn open_file(&mut self, path: PathBuf) {
        self.selected = Some(path.clone());
        self.entries.clear();
        self.view.clear();
        self.pending.clear();
        self.offset = 0;
        self.selected_line = None;
        match load_tail(&path) {
            Ok((offset, pending, lines)) => {
                self.offset = offset;
                self.pending = pending;
                self.entries = lines;
                self.status = format!("{}  ·  {}줄", path.display(), self.entries.len());
            }
            Err(e) => {
                self.status = format!("열기 실패: {e}");
            }
        }
        self.rebuild_view(true);
    }

    fn poll_file(&mut self) {
        let Some(path) = self.selected.clone() else {
            return;
        };
        match append_new(&path, self.offset, &mut self.pending) {
            Ok((new_offset, mut lines)) => {
                if new_offset < self.offset {
                    self.open_file(path);
                    return;
                }
                self.offset = new_offset;
                if !lines.is_empty() {
                    self.entries.append(&mut lines);
                    trim_front(&mut self.entries, MAX_LINES);
                    self.rebuild_view(false);
                    self.status = format!("{}  ·  {}줄 (실시간)", path.display(), self.entries.len());
                }
            }
            Err(e) => self.status = format!("읽기 오류: {e}"),
        }
    }

    fn filter_key(&self) -> String {
        format!(
            "{}|{}{}{}{}{}|{}",
            self.search,
            self.show_error as u8,
            self.show_warn as u8,
            self.show_info as u8,
            self.show_debug as u8,
            self.show_packets as u8,
            self.show_trace as u8
        )
    }

    fn rebuild_view(&mut self, force: bool) {
        let key = self.filter_key();
        if !force && key == self.last_filter {
            return;
        }
        self.last_filter = key;
        let q = self.search.to_lowercase();
        self.view = self
            .entries
            .iter()
            .enumerate()
            .filter(|(_, e)| self.matches(e, &q))
            .map(|(i, _)| i)
            .collect();
    }

    fn matches(&self, e: &LogEntry, q: &str) -> bool {
        if e.is_packet() && !self.show_packets {
            return false;
        }
        let level_ok = match e.level {
            LogLevel::Error => self.show_error,
            LogLevel::Warn => self.show_warn,
            LogLevel::Info => self.show_info,
            LogLevel::Debug => self.show_debug,
            LogLevel::Trace => self.show_trace,
            LogLevel::Unknown => self.show_info,
        };
        if !level_ok {
            return false;
        }
        if q.is_empty() {
            return true;
        }
        e.message.to_lowercase().contains(q)
            || e.target.to_lowercase().contains(q)
            || e.raw.to_lowercase().contains(q)
    }

    fn pick_folder(&mut self) {
        if let Some(dir) = rfd::FileDialog::new().set_directory(&self.log_dir).pick_folder() {
            self.log_dir = dir;
            self.scan_files();
            if let Some(first) = self.files.first().cloned() {
                self.open_file(first);
            } else {
                self.selected = None;
                self.entries.clear();
                self.status = "이 폴더에 로그 파일이 없습니다.".into();
            }
        }
    }

    fn open_in_explorer(&self) {
        let dir = &self.log_dir;
        let _ = {
            #[cfg(windows)]
            {
                std::process::Command::new("explorer").arg(dir).spawn()
            }
            #[cfg(not(windows))]
            {
                std::process::Command::new("xdg-open").arg(dir).spawn()
            }
        };
    }
}

impl eframe::App for LogViewerApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if self.follow {
            ctx.request_repaint_after(POLL);
            if self.last_scan.elapsed() > Duration::from_secs(2) {
                self.scan_files();
            }
            if self.last_poll.elapsed() >= POLL {
                self.poll_file();
                self.last_poll = Instant::now();
            }
        }

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.add_space(4.0);
            ui.horizontal(|ui| {
                ui.heading("서버 로그 뷰어");
                ui.separator();
                ui.label(RichText::new(self.log_dir.display().to_string()).small().weak());
                if ui.button("폴더 선택").clicked() {
                    self.pick_folder();
                }
                if ui.button("탐색기").clicked() {
                    self.open_in_explorer();
                }
                if ui.button("새로고침").clicked() {
                    self.scan_files();
                    if let Some(p) = self.selected.clone() {
                        self.open_file(p);
                    }
                }
                ui.separator();
                ui.checkbox(&mut self.follow, "실시간");
            });
            ui.horizontal(|ui| {
                ui.label("검색");
                ui.add(egui::TextEdit::singleline(&mut self.search).desired_width(220.0));
                ui.separator();
                ui.checkbox(&mut self.show_error, "ERROR");
                ui.checkbox(&mut self.show_warn, "WARN");
                ui.checkbox(&mut self.show_info, "INFO");
                ui.checkbox(&mut self.show_debug, "DEBUG");
                ui.checkbox(&mut self.show_packets, "패킷");
                ui.separator();
                ui.label(
                    RichText::new(format!("표시 {} / 전체 {}", self.view.len(), self.entries.len()))
                        .small(),
                );
            });
            ui.add_space(2.0);
        });

        egui::TopBottomPanel::bottom("bottom").min_height(88.0).show(ctx, |ui| {
            ui.add_space(4.0);
            ui.label(RichText::new(&self.status).small().weak());
            if let Some(i) = self.selected_line {
                if let Some(e) = self.entries.get(i) {
                    ScrollArea::vertical().max_height(72.0).show(ui, |ui| {
                        ui.monospace(&e.raw);
                    });
                }
            } else {
                ui.label(
                    RichText::new("줄을 클릭하면 원문 JSON이 여기에 표시됩니다. 패킷 로그는 DEBUG + 패킷 체크.")
                        .small()
                        .weak(),
                );
            }
        });

        egui::SidePanel::left("files")
            .resizable(true)
            .default_width(240.0)
            .show(ctx, |ui| {
                ui.add_space(4.0);
                ui.label(RichText::new("로그 파일").strong());
                ui.separator();
                ScrollArea::vertical().show(ui, |ui| {
                    if self.files.is_empty() {
                        ui.label("파일 없음");
                    }
                    let files = self.files.clone();
                    for path in files {
                        let name = path
                            .file_name()
                            .map(|s| s.to_string_lossy().into_owned())
                            .unwrap_or_else(|| path.display().to_string());
                        let selected = self.selected.as_ref() == Some(&path);
                        if ui.selectable_label(selected, name).clicked() {
                            self.open_file(path);
                        }
                    }
                });
            });

        egui::CentralPanel::default().show(ctx, |ui| {
            self.rebuild_view(false);
            let stick = self.follow && self.selected_line.is_none();
            ScrollArea::both()
                .auto_shrink([false, false])
                .stick_to_bottom(stick)
                .show(ui, |ui| {
                    ui.style_mut().wrap_mode = Some(egui::TextWrapMode::Extend);
                    let rows = self.view.clone();
                    for idx in rows {
                        let Some(entry) = self.entries.get(idx) else {
                            continue;
                        };
                        let color = level_color(entry.level);
                        let selected = self.selected_line == Some(idx);
                        let line = format!(
                            "{:<16} {:<5} {:<28} {}",
                            if entry.ts.is_empty() { "-" } else { &entry.ts },
                            entry.level.as_str(),
                            truncate(&entry.target, 28),
                            entry.message
                        );
                        let galley = ui.fonts(|f| {
                            f.layout_no_wrap(
                                line,
                                egui::FontId::monospace(13.0),
                                color,
                            )
                        });
                        let (rect, resp) = ui.allocate_exact_size(galley.size(), Sense::click());
                        if selected {
                            ui.painter()
                                .rect_filled(rect, 0.0, ui.visuals().selection.bg_fill);
                        }
                        ui.painter().galley(rect.min, galley, color);
                        if resp.clicked() {
                            self.selected_line = Some(idx);
                        }
                    }
                });
        });
    }
}

fn level_color(level: LogLevel) -> Color32 {
    match level {
        LogLevel::Error => Color32::from_rgb(232, 88, 88),
        LogLevel::Warn => Color32::from_rgb(232, 168, 64),
        LogLevel::Info => Color32::from_rgb(210, 214, 220),
        LogLevel::Debug => Color32::from_rgb(130, 150, 170),
        LogLevel::Trace => Color32::from_rgb(110, 110, 120),
        LogLevel::Unknown => Color32::from_rgb(180, 180, 180),
    }
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    s.chars().take(max.saturating_sub(1)).collect::<String>() + "…"
}

fn list_log_files(dir: &Path) -> Vec<PathBuf> {
    let mut files = Vec::new();
    let Ok(rd) = fs::read_dir(dir) else {
        return files;
    };
    for ent in rd.flatten() {
        let path = ent.path();
        if !path.is_file() {
            continue;
        }
        let name = path.file_name().and_then(|s| s.to_str()).unwrap_or("");
        if name.ends_with(".json") {
            continue;
        }
        files.push(path);
    }
    files.sort_by(|a, b| {
        let ma = fs::metadata(a).and_then(|m| m.modified()).ok();
        let mb = fs::metadata(b).and_then(|m| m.modified()).ok();
        mb.cmp(&ma)
    });
    files
}

fn load_tail(path: &Path) -> std::io::Result<(u64, String, Vec<LogEntry>)> {
    let mut f = File::open(path)?;
    let len = f.metadata()?.len();
    let start = len.saturating_sub(TAIL_BYTES);
    f.seek(SeekFrom::Start(start))?;
    let mut buf = String::new();
    f.read_to_string(&mut buf)?;
    let offset = start + buf.len() as u64;
    let (pending, lines) = split_complete(&buf, start > 0);
    Ok((offset, pending, lines))
}

fn append_new(path: &Path, offset: u64, pending: &mut String) -> std::io::Result<(u64, Vec<LogEntry>)> {
    let mut f = File::open(path)?;
    let len = f.metadata()?.len();
    if len < offset {
        return Ok((0, Vec::new()));
    }
    if len == offset && pending.is_empty() {
        return Ok((offset, Vec::new()));
    }
    f.seek(SeekFrom::Start(offset))?;
    let mut buf = String::new();
    f.read_to_string(&mut buf)?;
    let new_offset = offset + buf.len() as u64;
    pending.push_str(&buf);
    let (rest, lines) = split_complete(pending, false);
    *pending = rest;
    Ok((new_offset, lines))
}

fn split_complete(text: &str, skip_first_partial: bool) -> (String, Vec<LogEntry>) {
    let mut parts: Vec<&str> = text.split('\n').collect();
    let rest = if text.ends_with('\n') {
        String::new()
    } else {
        parts.pop().unwrap_or("").to_string()
    };
    let start = if skip_first_partial && !parts.is_empty() { 1 } else { 0 };
    let entries = parts
        .into_iter()
        .skip(start)
        .filter(|l| !l.trim().is_empty())
        .map(LogEntry::parse_line)
        .collect();
    (rest, entries)
}

fn trim_front(entries: &mut Vec<LogEntry>, max: usize) {
    if entries.len() > max {
        let drop = entries.len() - max;
        entries.drain(0..drop);
    }
}
