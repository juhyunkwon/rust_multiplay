//! 테스트용 TCP 클라

use anyhow::{bail, Context};
use std::collections::VecDeque;
use tokio::io::AsyncReadExt;
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::net::TcpStream;
use tokio::time::{timeout, Duration};
use x0_core::net::write_json_line;
use x0_core::protocol::{MessageType, NetworkMessage};
use x0_core::recv_buffer::RecvLineBuffer;
use x0_core::{MessageSerializer, PacketCrypto};

pub struct LineClient {
    reader: OwnedReadHalf,
    writer: OwnedWriteHalf,
    crypto: PacketCrypto,
    buf: RecvLineBuffer,
    scratch: Vec<u8>,
    inbox: VecDeque<NetworkMessage>,
}

impl LineClient {
    pub async fn connect_crypto(addr: &str, crypto: PacketCrypto) -> anyhow::Result<Self> {
        let stream = TcpStream::connect(addr)
            .await
            .with_context(|| format!("connect {addr}"))?;
        let _ = stream.set_nodelay(true);
        let (reader, writer) = stream.into_split();
        Ok(Self {
            reader,
            writer,
            crypto,
            buf: RecvLineBuffer::new(8192),
            scratch: vec![0; 4096],
            inbox: VecDeque::new(),
        })
    }

    pub async fn send(&mut self, ty: MessageType, payload: serde_json::Value) -> anyhow::Result<()> {
        let json = NetworkMessage::new(ty, payload).serialize();
        write_json_line(&mut self.writer, &self.crypto, &json)
            .await
            .context("send")?;
        Ok(())
    }

    pub async fn recv_type(
        &mut self,
        want: MessageType,
        wait: Duration,
    ) -> anyhow::Result<NetworkMessage> {
        if let Some(i) = self.inbox.iter().position(|m| m.msg_type == want) {
            return Ok(self.inbox.remove(i).expect("inbox"));
        }
        let deadline = tokio::time::Instant::now() + wait;
        loop {
            let left = deadline.saturating_duration_since(tokio::time::Instant::now());
            if left.is_zero() {
                bail!("{} 수신 타임아웃", want as u8);
            }
            let msg = timeout(left, self.read_one())
                .await
                .with_context(|| format!("{} 수신 타임아웃", want as u8))??;
            if msg.msg_type == want {
                return Ok(msg);
            }
            self.inbox.push_back(msg);
        }
    }

    async fn read_one(&mut self) -> anyhow::Result<NetworkMessage> {
        loop {
            if let Some(line) = self.buf.read_line() {
                let line = line.trim().to_string();
                if line.is_empty() {
                    continue;
                }
                let plain = self.crypto.decode_from_receive(&line);
                return MessageSerializer::parse_network_message(&plain)
                    .context("JSON 파싱 실패");
            }
            let n = self.reader.read(&mut self.scratch).await?;
            if n == 0 {
                bail!("연결 종료");
            }
            if !self.buf.on_write(&self.scratch[..n]) {
                anyhow::bail!("수신 버퍼 상한 초과");
            }
        }
    }
}
