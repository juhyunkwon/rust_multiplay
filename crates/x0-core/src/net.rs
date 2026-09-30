//! TCP 로 JSON 한 줄 보내기/받기

use crate::crypto::PacketCrypto;
use crate::protocol::{MessageType, NetworkMessage};
use serde::Serialize;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

pub fn encode_line(crypto: &PacketCrypto, json: &str) -> Vec<u8> {
    let mut encoded = crypto.encode_for_send(json);
    encoded.push('\n');
    encoded.into_bytes()
}

pub fn response_json(msg_type: MessageType, payload: impl Serialize) -> String {
    NetworkMessage::new(msg_type, payload).serialize()
}

pub async fn write_json_line<W: AsyncWriteExt + Unpin>(
    writer: &mut W,
    crypto: &PacketCrypto,
    json: &str,
) -> std::io::Result<()> {
    writer.write_all(&encode_line(crypto, json)).await?;
    writer.flush().await
}

pub async fn read_some<R: AsyncReadExt + Unpin>(
    reader: &mut R,
    buf: &mut [u8],
) -> std::io::Result<usize> {
    reader.read(buf).await
}
