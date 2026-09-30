//! 받은 바이트 모아뒀다가 줄 단위로 꺼냄

pub const MAX_RECV_LINE_BYTES: usize = 1024 * 1024;

pub struct RecvLineBuffer {
    buffer: Vec<u8>,
    write_pos: usize,
    delimiter: u8,
    max_bytes: usize,
}

impl RecvLineBuffer {
    pub fn new(capacity: usize) -> Self {
        Self::with_limit(capacity, MAX_RECV_LINE_BYTES)
    }

    pub fn with_limit(capacity: usize, max_bytes: usize) -> Self {
        let max_bytes = max_bytes.max(16);
        Self {
            buffer: vec![0; capacity.max(16).min(max_bytes)],
            write_pos: 0,
            delimiter: b'\n',
            max_bytes,
        }
    }

    /// 버퍼에 추가. 크기 넘으면 false
    pub fn on_write(&mut self, data: &[u8]) -> bool {
        let needed = self.write_pos.saturating_add(data.len());
        if needed > self.max_bytes {
            return false;
        }
        if needed > self.buffer.len() {
            let grow = needed.max(self.buffer.len().saturating_mul(2));
            self.buffer.resize(grow.min(self.max_bytes), 0);
        }
        self.buffer[self.write_pos..self.write_pos + data.len()].copy_from_slice(data);
        self.write_pos += data.len();
        true
    }

    /// 한 줄 꺼냄. 아직 없으면 None
    pub fn read_line(&mut self) -> Option<String> {
        let mut i = 0;
        while i < self.write_pos && self.buffer[i] != self.delimiter {
            i += 1;
        }
        if i >= self.write_pos {
            return None;
        }
        let line = String::from_utf8_lossy(&self.buffer[..i]).into_owned();
        let remain = self.write_pos - (i + 1);
        if remain > 0 {
            self.buffer.copy_within(i + 1..self.write_pos, 0);
        }
        self.write_pos = remain;
        Some(line)
    }

    pub fn clean(&mut self) {
        self.write_pos = 0;
    }

    pub fn data_size(&self) -> usize {
        self.write_pos
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_lines() {
        let mut b = RecvLineBuffer::new(64);
        assert!(b.on_write(b"hello\nwor"));
        assert_eq!(b.read_line().as_deref(), Some("hello"));
        assert_eq!(b.read_line(), None);
        assert!(b.on_write(b"ld\n"));
        assert_eq!(b.read_line().as_deref(), Some("world"));
    }

    #[test]
    fn rejects_over_limit() {
        let mut b = RecvLineBuffer::with_limit(8, 16);
        assert!(!b.on_write(&[0u8; 17]));
        assert_eq!(b.data_size(), 0);
        assert!(b.on_write(b"ok\n"));
        assert_eq!(b.read_line().as_deref(), Some("ok"));
    }
}
