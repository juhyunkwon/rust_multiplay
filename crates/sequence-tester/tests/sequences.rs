use sequence_tester::{ensure_tracing, run_suite_with, Suite, Target};

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn dedicated_sequence_passes() {
    ensure_tracing();
    let reports = run_suite_with(Suite::Dedicated, Target::InProcess).await;
    assert!(reports[0].ok(), "{:?}", reports[0]);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn p2p_sequence_has_no_fails() {
    ensure_tracing();
    let reports = run_suite_with(Suite::P2p, Target::InProcess).await;
    assert!(reports[0].ok(), "{:?}", reports[0]);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn relay_sequence_passes() {
    ensure_tracing();
    let reports = run_suite_with(Suite::Relay, Target::InProcess).await;
    assert!(reports[0].ok(), "{:?}", reports[0]);
}
