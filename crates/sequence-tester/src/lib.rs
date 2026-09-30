mod accounts;
mod client;
mod dedicated;
mod harness;
mod p2p;
mod relay;
mod report;
mod suite;

pub use accounts::{
    create_account, create_many, create_pair, ensure_default_accounts, login_account, NewAccount,
    DEFAULT_PASS_A, DEFAULT_PASS_B, DEFAULT_USER_A, DEFAULT_USER_B,
};
pub use harness::Target;
pub use report::{SequenceReport, Step, StepStatus};
pub use suite::{print_reports, Suite};

pub async fn run_suite(suite: Suite) -> Vec<SequenceReport> {
    run_suite_with(suite, Target::Live).await
}

pub async fn run_suite_with(suite: Suite, target: Target) -> Vec<SequenceReport> {
    let host = if target == Target::Live {
        match harness::ensure_live_host().await {
            Ok(h) => Some(h),
            Err(e) => return vec![harness::live_unavailable("실제 서버", e)],
        }
    } else {
        None
    };

    let mut out = Vec::new();
    match suite {
        Suite::Dedicated => out.push(dedicated::run(target, host.as_ref()).await),
        Suite::P2p => out.push(p2p::run(target, host.as_ref()).await),
        Suite::Relay => out.push(relay::run(target, host.as_ref()).await),
        Suite::All => {
            out.push(dedicated::run(target, host.as_ref()).await);
            out.push(p2p::run(target, host.as_ref()).await);
            out.push(relay::run(target, host.as_ref()).await);
        }
    }
    out
}

pub fn ensure_tracing() {
    let _ = tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("warn")),
        )
        .try_init();
}
