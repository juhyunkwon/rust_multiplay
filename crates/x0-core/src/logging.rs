//! 콘솔 로그 + 날짜별 JSON 파일 로그 (log-viewer 가 읽음)

use std::path::Path;
use tracing_appender::non_blocking::WorkerGuard;
use tracing_subscriber::{fmt, layer::SubscriberExt, util::SubscriberInitExt, EnvFilter};

/// WorkerGuard 가 살아 있어야 파일에 써짐
pub struct LogGuard {
    _guard: WorkerGuard,
}

/// 파일은 log_dir/file_prefix.YYYY-MM-DD 에 JSON 으로, 콘솔은 보기 편하게
pub fn init(service: &str, log_dir: impl AsRef<Path>, file_prefix: &str) -> LogGuard {
    let log_dir = log_dir.as_ref();
    let _ = std::fs::create_dir_all(log_dir);

    let prefix = file_prefix
        .trim()
        .trim_end_matches(".log")
        .trim_end_matches('-')
        .trim();
    let prefix = if prefix.is_empty() { service } else { prefix };

    let appender = tracing_appender::rolling::daily(log_dir, prefix);
    let (non_blocking, guard) = tracing_appender::non_blocking(appender);

    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info"));

    let stdout_layer = fmt::layer()
        .with_target(true)
        .with_ansi(true);

    let file_layer = fmt::layer()
        .json()
        .with_ansi(false)
        .with_target(true)
        .with_current_span(false)
        .with_span_list(false)
        .with_writer(non_blocking);

    tracing_subscriber::registry()
        .with(filter)
        .with(stdout_layer)
        .with(file_layer)
        .init();

    tracing::info!(
        service,
        path = %log_dir.display(),
        prefix,
        "파일 로그 시작 (JSON, 일별 롤링)"
    );

    LogGuard { _guard: guard }
}
