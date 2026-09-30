//! 패킷 암호화 (AES-256-CBC)
//!
//! 키 없으면 평문. 키 문자열을 SHA-256 해서 32바이트 키로 씀
//! 보내는 형식은 Base64(IV 16바이트 + 암호문), PKCS7 패딩

use aes::Aes256;
use base64::{engine::general_purpose::STANDARD as B64, Engine};
use cbc::{Decryptor, Encryptor};
use cipher::{block_padding::Pkcs7, BlockDecryptMut, BlockEncryptMut, KeyIvInit};
use sha2::{Digest, Sha256};

const IV_LEN: usize = 16;

pub struct PacketCrypto {
    key: Option<[u8; 32]>,
}

impl Clone for PacketCrypto {
    fn clone(&self) -> Self {
        Self { key: self.key }
    }
}

impl PacketCrypto {
    pub fn new(key: Option<&str>) -> Self {
        let key = key
            .map(str::trim)
            .filter(|k| !k.is_empty())
            .map(|k| {
                let hash = Sha256::digest(k.as_bytes());
                let mut out = [0u8; 32];
                out.copy_from_slice(&hash);
                out
            });
        Self { key }
    }

    pub fn is_enabled(&self) -> bool {
        self.key.is_some()
    }

    pub fn encrypt(&self, plain_text: &str) -> String {
        let Some(key) = self.key else {
            return plain_text.to_string();
        };

        let mut iv = [0u8; IV_LEN];
        fill_random(&mut iv);

        let encryptor = Encryptor::<Aes256>::new((&key).into(), (&iv).into());
        let cipher_bytes = encryptor.encrypt_padded_vec_mut::<Pkcs7>(plain_text.as_bytes());

        let mut result = Vec::with_capacity(IV_LEN + cipher_bytes.len());
        result.extend_from_slice(&iv);
        result.extend_from_slice(&cipher_bytes);
        B64.encode(result)
    }

    pub fn decrypt(&self, cipher_base64: &str) -> String {
        let Some(key) = self.key else {
            return cipher_base64.to_string();
        };

        let Ok(all_bytes) = B64.decode(cipher_base64.trim()) else {
            return cipher_base64.to_string();
        };
        if all_bytes.len() < IV_LEN {
            return cipher_base64.to_string();
        }

        let (iv, cipher) = all_bytes.split_at(IV_LEN);
        let iv: [u8; IV_LEN] = match iv.try_into() {
            Ok(v) => v,
            Err(_) => return cipher_base64.to_string(),
        };

        let decryptor = Decryptor::<Aes256>::new((&key).into(), (&iv).into());
        match decryptor.decrypt_padded_vec_mut::<Pkcs7>(cipher) {
            Ok(plain) => String::from_utf8(plain).unwrap_or_else(|_| cipher_base64.to_string()),
            Err(_) => cipher_base64.to_string(),
        }
    }

    pub fn encode_for_send(&self, json: &str) -> String {
        if self.is_enabled() {
            self.encrypt(json)
        } else {
            json.to_string()
        }
    }

    pub fn decode_from_receive(&self, line: &str) -> String {
        if !self.is_enabled() || line.is_empty() {
            return line.to_string();
        }
        if !looks_like_base64(line) {
            return line.to_string();
        }
        self.decrypt(line)
    }
}

fn looks_like_base64(s: &str) -> bool {
    if s.len() < 20 {
        return false;
    }
    s.chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '+' || c == '/' || c == '=')
}

fn fill_random(buf: &mut [u8]) {
    use rand::RngCore;
    rand::thread_rng().fill_bytes(buf);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plaintext_passthrough() {
        let c = PacketCrypto::new(None);
        assert!(!c.is_enabled());
        assert_eq!(c.encode_for_send("hello"), "hello");
        assert_eq!(c.decode_from_receive("hello"), "hello");
    }

    #[test]
    fn roundtrip() {
        let c = PacketCrypto::new(Some("secret-key"));
        let json = r#"{"t":1,"p":{"id":"ab"},"ts":1}"#;
        let enc = c.encrypt(json);
        assert_ne!(enc, json);
        assert!(looks_like_base64(&enc));
        assert_eq!(c.decrypt(&enc), json);
        assert_eq!(c.decode_from_receive(&enc), json);
    }
}
