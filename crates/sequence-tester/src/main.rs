use sequence_tester::{
    create_account, ensure_default_accounts, ensure_tracing, login_account, print_reports, run_suite,
    SequenceReport, StepStatus, Suite, DEFAULT_PASS_A, DEFAULT_PASS_B, DEFAULT_USER_A,
    DEFAULT_USER_B,
};

fn main() {
    ensure_tracing();
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "--create-user" || a == "--ensure-accounts") {
        let rt = tokio::runtime::Runtime::new().expect("tokio");
        let ok = rt.block_on(run_account_cli(&args));
        if args.iter().any(|a| a == "--cli" || a == "-c") {
            if !ok {
                std::process::exit(1);
            }
        } else {
            std::process::exit(if ok { 0 } else { 1 });
        }
    }
    if args.iter().any(|a| a == "--cli" || a == "-c") {
        let in_process = args.iter().any(|a| a == "--in-process");
        let suite = args
            .iter()
            .find(|a| !a.starts_with('-'))
            .and_then(|s| Suite::parse(s))
            .unwrap_or(Suite::All);
        let target = if in_process {
            sequence_tester::Target::InProcess
        } else {
            sequence_tester::Target::Live
        };
        let rt = tokio::runtime::Runtime::new().expect("tokio");
        let reports = rt.block_on(sequence_tester::run_suite_with(suite, target));
        let ok = print_reports(&reports);
        std::process::exit(if ok { 0 } else { 1 });
    }

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1080.0, 840.0])
            .with_min_inner_size([920.0, 680.0])
            .with_title("X0 시퀀스 테스트"),
        ..Default::default()
    };
    let _ = eframe::run_native(
        "x0-sequence-tester",
        options,
        Box::new(|cc| {
            setup_korean_fonts(&cc.egui_ctx);
            cc.egui_ctx.set_visuals(egui::Visuals::light());
            Ok(Box::new(SeqApp::default()))
        }),
    );
}

struct AccountRow {
    user: String,
    pass: String,
    nick: String,
}

impl AccountRow {
    fn new(user: &str, pass: &str, nick: &str) -> Self {
        Self {
            user: user.to_string(),
            pass: pass.to_string(),
            nick: nick.to_string(),
        }
    }

    fn blank() -> Self {
        Self {
            user: String::new(),
            pass: DEFAULT_PASS_A.to_string(),
            nick: String::new(),
        }
    }
}

struct SeqApp {
    running: bool,
    reports: Vec<SequenceReport>,
    rx: Option<std::sync::mpsc::Receiver<Vec<SequenceReport>>>,
    rows: Vec<AccountRow>,
    shop_id: String,
    account_busy: bool,
    account_msg: String,
    account_ok: bool,
    account_rx: Option<std::sync::mpsc::Receiver<(bool, String)>>,
}

impl Default for SeqApp {
    fn default() -> Self {
        Self {
            running: false,
            reports: Vec::new(),
            rx: None,
            rows: vec![
                AccountRow::new(DEFAULT_USER_A, DEFAULT_PASS_A, DEFAULT_USER_A),
                AccountRow::new(DEFAULT_USER_B, DEFAULT_PASS_B, DEFAULT_USER_B),
            ],
            shop_id: "0".to_string(),
            account_busy: false,
            account_msg: String::new(),
            account_ok: true,
            account_rx: None,
        }
    }
}

impl SeqApp {
    fn filled(&self) -> Vec<&AccountRow> {
        self.rows
            .iter()
            .filter(|r| !r.user.trim().is_empty())
            .collect()
    }

    fn start(&mut self, suite: Suite) {
        if self.running {
            return;
        }
        let filled = self.filled();
        if matches!(suite, Suite::Dedicated | Suite::All) && filled.len() < 2 {
            self.account_ok = false;
            self.account_msg = "Dedicated는 아이디가 있는 계정이 2개 필요합니다. [+] 로 추가하세요.".into();
            return;
        }
        let ua = filled
            .first()
            .map(|r| r.user.clone())
            .unwrap_or_else(|| DEFAULT_USER_A.to_string());
        let pa = filled
            .first()
            .map(|r| r.pass.clone())
            .unwrap_or_else(|| DEFAULT_PASS_A.to_string());
        let ub = filled
            .get(1)
            .map(|r| r.user.clone())
            .unwrap_or_else(|| DEFAULT_USER_B.to_string());
        let pb = filled
            .get(1)
            .map(|r| r.pass.clone())
            .unwrap_or_else(|| DEFAULT_PASS_B.to_string());
        let (tx, rx) = std::sync::mpsc::channel();
        self.rx = Some(rx);
        self.running = true;
        self.reports.clear();
        std::thread::spawn(move || {
            std::env::set_var("SEQ_USER_A", ua);
            std::env::set_var("SEQ_PASS_A", pa);
            std::env::set_var("SEQ_USER_B", ub);
            std::env::set_var("SEQ_PASS_B", pb);
            let rt = tokio::runtime::Runtime::new().expect("tokio");
            let reports = rt.block_on(run_suite(suite));
            let _ = tx.send(reports);
        });
    }

    fn create_one_at(&mut self, idx: usize) {
        if self.account_busy {
            return;
        }
        let Some(row) = self.rows.get(idx) else {
            return;
        };
        if row.user.trim().is_empty() {
            self.account_ok = false;
            self.account_msg = "아이디가 비어 있습니다.".into();
            return;
        }
        let user = row.user.clone();
        let pass = row.pass.clone();
        let nick = if row.nick.trim().is_empty() {
            user.clone()
        } else {
            row.nick.clone()
        };
        let (tx, rx) = std::sync::mpsc::channel();
        self.account_rx = Some(rx);
        self.account_busy = true;
        self.account_msg = format!("{user} DB 생성/갱신 중…");
        std::thread::spawn(move || {
            let rt = tokio::runtime::Runtime::new().expect("tokio");
            let msg = match rt.block_on(create_account(&user, &pass, &nick)) {
                Ok(r) => (true, r.describe()),
                Err(e) => (false, format!("생성 실패: {e:#}")),
            };
            let _ = tx.send(msg);
        });
    }

    fn login_one_at(&mut self, idx: usize) {
        if self.account_busy {
            return;
        }
        let Some(row) = self.rows.get(idx) else {
            return;
        };
        if row.user.trim().is_empty() {
            self.account_ok = false;
            self.account_msg = "아이디가 비어 있습니다.".into();
            return;
        }
        let user = row.user.clone();
        let pass = row.pass.clone();
        let shop = self.shop_id.parse::<i64>().unwrap_or(0);
        let (tx, rx) = std::sync::mpsc::channel();
        self.account_rx = Some(rx);
        self.account_busy = true;
        self.account_msg = format!("{user} 로그인 중…");
        std::thread::spawn(move || {
            let rt = tokio::runtime::Runtime::new().expect("tokio");
            let msg = match rt.block_on(login_account(&user, &pass, shop)) {
                Ok(s) => (true, s),
                Err(e) => (false, format!("로그인 실패: {e:#}")),
            };
            let _ = tx.send(msg);
        });
    }

    fn fill_defaults(&mut self) {
        self.rows = vec![
            AccountRow::new(DEFAULT_USER_A, DEFAULT_PASS_A, DEFAULT_USER_A),
            AccountRow::new(DEFAULT_USER_B, DEFAULT_PASS_B, DEFAULT_USER_B),
        ];
        self.shop_id = "0".to_string();
        self.account_ok = true;
        self.account_msg = format!(
            "기본값  {DEFAULT_USER_A} / {DEFAULT_PASS_A}  ·  {DEFAULT_USER_B} / {DEFAULT_PASS_B}"
        );
    }
}

impl eframe::App for SeqApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if let Some(rx) = &self.rx {
            if let Ok(reports) = rx.try_recv() {
                self.reports = reports;
                self.running = false;
                self.rx = None;
            } else {
                ctx.request_repaint_after(std::time::Duration::from_millis(80));
            }
        }
        if let Some(rx) = &self.account_rx {
            if let Ok((ok, msg)) = rx.try_recv() {
                self.account_ok = ok;
                self.account_msg = msg;
                self.account_busy = false;
                self.account_rx = None;
            } else {
                ctx.request_repaint_after(std::time::Duration::from_millis(80));
            }
        }

        let panel = egui::Frame::new()
            .fill(egui::Color32::from_rgb(248, 249, 251))
            .inner_margin(egui::Margin::same(14));

        egui::TopBottomPanel::top("top")
            .frame(panel)
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.heading("시퀀스 테스트");
                    ui.add_space(12.0);
                    ui.add_enabled_ui(!self.running, |ui| {
                        suite_btn(ui, "Dedicated", || self.start(Suite::Dedicated));
                        suite_btn(ui, "P2P", || self.start(Suite::P2p));
                        suite_btn(ui, "릴레이", || self.start(Suite::Relay));
                        suite_btn(ui, "전체", || self.start(Suite::All));
                    });
                    if self.running {
                        ui.add_space(8.0);
                        ui.spinner();
                        ui.colored_label(egui::Color32::from_rgb(40, 90, 180), "실행 중");
                    }
                });
                ui.add_space(4.0);
                ui.label(
                    egui::RichText::new("실제 server-host(19050)에 붙습니다. 테스트가 끝나도 서버는 유지됩니다.")
                        .color(egui::Color32::from_rgb(90, 96, 108))
                        .small(),
                );

                ui.add_space(10.0);
                egui::Frame::new()
                    .fill(egui::Color32::WHITE)
                    .stroke(egui::Stroke::new(1.0, egui::Color32::from_rgb(214, 218, 226)))
                    .inner_margin(egui::Margin::same(12))
                    .corner_radius(6.0)
                    .show(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.strong("계정");
                            ui.label(
                                egui::RichText::new("생성=DB  ·  로그인=서버  ·  삭제=목록만")
                                    .color(egui::Color32::from_rgb(90, 96, 108))
                                    .small(),
                            );
                            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                ui.add(
                                    egui::TextEdit::singleline(&mut self.shop_id)
                                        .desired_width(48.0),
                                );
                                ui.label(
                                    egui::RichText::new("shop")
                                        .color(egui::Color32::from_rgb(90, 96, 108)),
                                );
                            });
                        });
                        ui.add_space(6.0);
                        ui.horizontal(|ui| {
                            ui.add_sized([22.0, 18.0], egui::Label::new(""));
                            col_head(ui, "아이디", 140.0);
                            col_head(ui, "비밀번호", 110.0);
                            col_head(ui, "닉네임", 140.0);
                        });
                        ui.add_space(4.0);
                        let mut remove_at = None;
                        let mut create_at = None;
                        let mut login_at = None;
                        let n = self.rows.len();
                        egui::ScrollArea::vertical()
                            .max_height(200.0)
                            .auto_shrink([false, true])
                            .show(ui, |ui| {
                                for i in 0..n {
                                    ui.horizontal(|ui| {
                                        ui.add_sized(
                                            [22.0, 26.0],
                                            egui::Label::new(
                                                egui::RichText::new(format!("{}", i + 1)).strong(),
                                            ),
                                        );
                                        ui.add(
                                            egui::TextEdit::singleline(&mut self.rows[i].user)
                                                .desired_width(140.0)
                                                .hint_text("아이디"),
                                        );
                                        ui.add(
                                            egui::TextEdit::singleline(&mut self.rows[i].pass)
                                                .desired_width(110.0)
                                                .password(true)
                                                .hint_text("비밀번호"),
                                        );
                                        ui.add(
                                            egui::TextEdit::singleline(&mut self.rows[i].nick)
                                                .desired_width(140.0)
                                                .hint_text("닉네임"),
                                        );
                                        ui.add_enabled_ui(!self.account_busy, |ui| {
                                            if ui
                                                .add_sized([56.0, 26.0], egui::Button::new("생성"))
                                                .clicked()
                                            {
                                                create_at = Some(i);
                                            }
                                            if ui
                                                .add_sized([64.0, 26.0], egui::Button::new("로그인"))
                                                .clicked()
                                            {
                                                login_at = Some(i);
                                            }
                                            if ui
                                                .add_sized([56.0, 26.0], egui::Button::new("삭제"))
                                                .on_hover_text("목록에서만 제거. DB는 그대로입니다.")
                                                .clicked()
                                            {
                                                remove_at = Some(i);
                                            }
                                        });
                                    });
                                    ui.add_space(3.0);
                                }
                            });
                        if let Some(i) = remove_at {
                            self.rows.remove(i);
                            if self.rows.is_empty() {
                                self.rows.push(AccountRow::blank());
                            }
                            self.account_ok = true;
                            self.account_msg = "목록에서 뺐습니다. DB는 삭제하지 않았습니다.".into();
                        }
                        if let Some(i) = create_at {
                            self.create_one_at(i);
                        }
                        if let Some(i) = login_at {
                            self.login_one_at(i);
                        }
                        ui.add_space(8.0);
                        ui.horizontal(|ui| {
                            ui.add_enabled_ui(!self.account_busy, |ui| {
                                if ui.button("+ 행 추가").clicked() {
                                    self.rows.push(AccountRow::blank());
                                }
                                if ui
                                    .button(format!(
                                        "기본값 넣기  ({DEFAULT_USER_A} / {DEFAULT_USER_B})"
                                    ))
                                    .clicked()
                                {
                                    self.fill_defaults();
                                }
                            });
                            if !self.account_msg.is_empty() {
                                ui.add_space(10.0);
                                let color = if self.account_ok {
                                    egui::Color32::from_rgb(30, 140, 70)
                                } else {
                                    egui::Color32::from_rgb(200, 70, 70)
                                };
                                ui.colored_label(color, &self.account_msg);
                            }
                        });
                    });
            });

        egui::CentralPanel::default()
            .frame(
                egui::Frame::new()
                    .fill(egui::Color32::from_rgb(242, 244, 247))
                    .inner_margin(egui::Margin::same(16)),
            )
            .show(ctx, |ui| {
                if self.reports.is_empty() && !self.running {
                    ui.vertical_centered(|ui| {
                        ui.add_space(48.0);
                        ui.label(
                            egui::RichText::new("Dedicated / P2P / 릴레이 / 전체를 누르면 시퀀스가 시작됩니다.")
                                .color(egui::Color32::from_rgb(90, 96, 108)),
                        );
                        ui.add_space(8.0);
                        ui.label(
                            egui::RichText::new("Dedicated 시퀀스는 목록 위부터 아이디가 있는 2개로 로그인합니다.")
                                .color(egui::Color32::from_rgb(90, 96, 108))
                                .small(),
                        );
                    });
                    return;
                }
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| {
                    for report in &self.reports {
                        let fail = report.failed();
                        let border = if fail > 0 {
                            egui::Color32::from_rgb(220, 140, 140)
                        } else {
                            egui::Color32::from_rgb(170, 200, 180)
                        };
                        egui::Frame::new()
                            .fill(egui::Color32::WHITE)
                            .stroke(egui::Stroke::new(1.0, border))
                            .inner_margin(egui::Margin::same(12))
                            .corner_radius(6.0)
                            .show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    ui.heading(&report.title);
                                    ui.with_layout(
                                        egui::Layout::right_to_left(egui::Align::Center),
                                        |ui| {
                                            ui.label(format!("{} steps", report.steps.len()));
                                            ui.colored_label(
                                                egui::Color32::from_rgb(232, 88, 88),
                                                format!("FAIL {}", report.failed()),
                                            );
                                            ui.colored_label(
                                                egui::Color32::from_rgb(80, 200, 120),
                                                format!("PASS {}", report.passed()),
                                            );
                                        },
                                    );
                                });
                                ui.add_space(8.0);
                                for step in &report.steps {
                                    let (mark, color) = match step.status {
                                        StepStatus::Pass => {
                                            ("PASS", egui::Color32::from_rgb(80, 200, 120))
                                        }
                                        StepStatus::Fail => {
                                            ("FAIL", egui::Color32::from_rgb(232, 88, 88))
                                        }
                                        StepStatus::Skip => {
                                            ("SKIP", egui::Color32::from_rgb(200, 170, 80))
                                        }
                                    };
                                    ui.horizontal(|ui| {
                                        ui.colored_label(
                                            color,
                                            egui::RichText::new(format!(" {mark} "))
                                                .strong()
                                                .monospace(),
                                        );
                                        ui.add(
                                            egui::Label::new(format!(
                                                "{}  ·  {}",
                                                step.name, step.detail
                                            ))
                                            .wrap(),
                                        );
                                    });
                                }
                            });
                        ui.add_space(10.0);
                    }
                });
            });
    }
}

fn suite_btn(ui: &mut egui::Ui, label: &str, mut on: impl FnMut()) {
    if ui.add_sized([96.0, 28.0], egui::Button::new(label)).clicked() {
        on();
    }
}

fn col_head(ui: &mut egui::Ui, label: &str, width: f32) {
    ui.add_sized(
        [width, 18.0],
        egui::Label::new(
            egui::RichText::new(label).color(egui::Color32::from_rgb(90, 96, 108)),
        ),
    );
}

async fn run_account_cli(args: &[String]) -> bool {
    let mut ok = true;
    if args.iter().any(|a| a == "--ensure-accounts") {
        match ensure_default_accounts().await {
            Ok(list) => {
                for r in list {
                    println!("{}", r.describe());
                }
            }
            Err(e) => {
                eprintln!("계정 A/B 생성 실패: {e:#}");
                ok = false;
            }
        }
    }
    if let Some(i) = args.iter().position(|a| a == "--create-user") {
        let id = args.get(i + 1).map(String::as_str).unwrap_or("");
        let pw = args.get(i + 2).map(String::as_str).unwrap_or("");
        let nick = args
            .get(i + 3)
            .filter(|s| !s.starts_with('-'))
            .map(String::as_str)
            .unwrap_or(id);
        if id.is_empty() || pw.is_empty() {
            eprintln!("사용: sequence-tester --create-user <id> <pw> [닉네임]");
            return false;
        }
        match create_account(id, pw, nick).await {
            Ok(r) => println!("{}", r.describe()),
            Err(e) => {
                eprintln!("계정 생성 실패: {e:#}");
                ok = false;
            }
        }
    }
    ok
}

fn setup_korean_fonts(ctx: &egui::Context) {
    let Some(bytes) = load_korean_font_bytes() else {
        return;
    };
    let mut fonts = egui::FontDefinitions::default();
    fonts
        .font_data
        .insert("kr".to_owned(), egui::FontData::from_owned(bytes).into());
    for family in [egui::FontFamily::Proportional, egui::FontFamily::Monospace] {
        fonts
            .families
            .entry(family)
            .or_default()
            .insert(0, "kr".to_owned());
    }
    ctx.set_fonts(fonts);
}

fn load_korean_font_bytes() -> Option<Vec<u8>> {
    use font_kit::family_name::FamilyName;
    use font_kit::properties::Properties;
    use font_kit::source::SystemSource;

    let names = [
        FamilyName::Title("Malgun Gothic".into()),
        FamilyName::Title("맑은 고딕".into()),
        FamilyName::Title("NanumGothic".into()),
        FamilyName::Title("Gulim".into()),
    ];
    if let Ok(handle) = SystemSource::new().select_best_match(&names, &Properties::new()) {
        if let Ok(font) = handle.load() {
            if let Some(data) = font.copy_font_data() {
                return Some(data.to_vec());
            }
        }
    }

    for path in [
        r"C:\Windows\Fonts\malgun.ttf",
        r"C:\Windows\Fonts\malgunsl.ttf",
        r"C:\Windows\Fonts\NanumGothic.ttf",
    ] {
        if let Ok(bytes) = std::fs::read(path) {
            return Some(bytes);
        }
    }
    None
}
