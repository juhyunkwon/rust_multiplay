//! 로그 한 줄 파싱 (tracing JSON 이나 그냥 텍스트)

use serde_json::Value;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LogLevel {
    Error,
    Warn,
    Info,
    Debug,
    Trace,
    Unknown,
}

impl LogLevel {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Error => "ERROR",
            Self::Warn => "WARN",
            Self::Info => "INFO",
            Self::Debug => "DEBUG",
            Self::Trace => "TRACE",
            Self::Unknown => "LOG",
        }
    }

    pub fn from_str(s: &str) -> Self {
        match s.to_ascii_uppercase().as_str() {
            "ERROR" => Self::Error,
            "WARN" | "WARNING" => Self::Warn,
            "INFO" => Self::Info,
            "DEBUG" => Self::Debug,
            "TRACE" => Self::Trace,
            _ => Self::Unknown,
        }
    }
}

#[derive(Debug, Clone)]
pub struct LogEntry {
    pub ts: String,
    pub level: LogLevel,
    pub target: String,
    pub message: String,
    pub kind: String,
    pub raw: String,
}

impl LogEntry {
    pub fn is_packet(&self) -> bool {
        self.kind.eq_ignore_ascii_case("packet")
            || self.target.contains("packet")
            || self.message.contains("수신") && self.target.contains("net")
    }

    pub fn parse_line(line: &str) -> Self {
        let line = line.trim_end_matches('\r');
        if line.is_empty() {
            return Self::plain(line);
        }
        if let Ok(v) = serde_json::from_str::<Value>(line) {
            if v.is_object() {
                return Self::from_json(line, &v);
            }
        }
        Self::plain(line)
    }

    fn from_json(raw: &str, v: &Value) -> Self {
        let ts = v
            .get("timestamp")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string();
        let level = v
            .get("level")
            .and_then(Value::as_str)
            .map(LogLevel::from_str)
            .unwrap_or(LogLevel::Unknown);
        let target = v
            .get("target")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string();
        let fields = v.get("fields").cloned().unwrap_or(Value::Null);
        let message = fields
            .get("message")
            .and_then(Value::as_str)
            .or_else(|| v.get("message").and_then(Value::as_str))
            .unwrap_or(raw)
            .to_string();
        let kind = fields
            .get("kind")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_string();
        Self {
            ts: shorten_ts(&ts),
            level,
            target,
            message,
            kind,
            raw: raw.to_string(),
        }
    }

    fn plain(line: &str) -> Self {
        let level = if line.contains("ERROR") || line.contains("error") {
            LogLevel::Error
        } else if line.contains("WARN") {
            LogLevel::Warn
        } else if line.contains("DEBUG") {
            LogLevel::Debug
        } else {
            LogLevel::Info
        };
        Self {
            ts: String::new(),
            level,
            target: String::new(),
            message: line.to_string(),
            kind: String::new(),
            raw: line.to_string(),
        }
    }
}

fn shorten_ts(ts: &str) -> String {
    // 2026-09-29T01:57:00.123456Z 를 09-29 01:57:00 으로
    if ts.len() >= 19 && ts.as_bytes().get(4) == Some(&b'-') {
        let date = &ts[5..10];
        let time = &ts[11..19];
        return format!("{date} {time}");
    }
    ts.to_string()
}
