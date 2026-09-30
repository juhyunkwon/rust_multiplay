use crate::report::{SequenceReport, StepStatus};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Suite {
    Dedicated,
    P2p,
    Relay,
    All,
}

impl Suite {
    pub fn parse(s: &str) -> Option<Self> {
        match s.to_ascii_lowercase().as_str() {
            "dedicated" | "d" | "데디" => Some(Self::Dedicated),
            "p2p" | "peer" => Some(Self::P2p),
            "relay" | "릴레이" => Some(Self::Relay),
            "all" | "전체" => Some(Self::All),
            _ => None,
        }
    }
}

pub fn print_reports(reports: &[SequenceReport]) -> bool {
    let mut ok = true;
    for r in reports {
        println!("=== {} ===", r.title);
        for step in &r.steps {
            let mark = match step.status {
                StepStatus::Pass => "PASS",
                StepStatus::Fail => "FAIL",
                StepStatus::Skip => "SKIP",
            };
            if step.status == StepStatus::Fail {
                ok = false;
            }
            println!("  [{mark}] {} — {}", step.name, step.detail);
        }
        println!();
    }
    ok
}
