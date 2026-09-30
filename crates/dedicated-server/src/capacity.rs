//! CPU/메모리 보고 방/인원 상한 정함

use x0_core::protocol::MAX_PLAYERS_PER_ROOM;
use sysinfo::System;

const MEMORY_RESERVE_RATIO: f64 = 0.3;
const BASE_MEMORY_PER_ROOM_MB: f64 = 5.0;
const MEMORY_PER_PLAYER_MB: f64 = 1.0;
const ROOMS_PER_CPU_CORE: i32 = 10;

#[derive(Debug, Clone)]
pub struct ServerCapacity {
    pub cpu_cores: i32,
    pub total_memory_mb: i64,
    pub available_memory_mb: i64,
    pub max_rooms: i32,
    pub max_players_per_room: i32,
}

impl ServerCapacity {
    pub fn new(max_rooms_override: Option<i32>, max_players_override: Option<i32>) -> Self {
        let cpu_cores = std::thread::available_parallelism()
            .map(|n| n.get() as i32)
            .unwrap_or(4);

        let mut sys = System::new();
        sys.refresh_memory();
        let total_memory_mb = (sys.total_memory() / 1024 / 1024) as i64;
        let available_memory_mb = (sys.available_memory() / 1024 / 1024) as i64;
        let (total_memory_mb, available_memory_mb) = if total_memory_mb <= 0 {
            (8192, 4096)
        } else {
            (total_memory_mb, available_memory_mb.max(1))
        };

        let mut usable_mb =
            (available_memory_mb.max((total_memory_mb as f64 * 0.4) as i64) as f64)
                * (1.0 - MEMORY_RESERVE_RATIO);
        if usable_mb < 200.0 {
            usable_mb = 512.0;
        }

        let rooms_by_cpu = (cpu_cores * ROOMS_PER_CPU_CORE).max(1);
        let rooms_by_mem = (usable_mb / BASE_MEMORY_PER_ROOM_MB) as i32;

        let mut max_rooms = max_rooms_override.unwrap_or(100.min(rooms_by_cpu.min(rooms_by_mem)));
        if max_rooms < 1 {
            max_rooms = 1;
        }

        let mem_per_room_mb = usable_mb / max_rooms as f64;
        let players_by_mem =
            ((mem_per_room_mb - BASE_MEMORY_PER_ROOM_MB) / MEMORY_PER_PLAYER_MB) as i32;
        let max_players_per_room = max_players_override.unwrap_or(players_by_mem.clamp(10, MAX_PLAYERS_PER_ROOM));

        Self {
            cpu_cores,
            total_memory_mb,
            available_memory_mb,
            max_rooms,
            max_players_per_room,
        }
    }

    pub fn for_tests() -> Self {
        Self {
            cpu_cores: 2,
            total_memory_mb: 1024,
            available_memory_mb: 512,
            max_rooms: 16,
            max_players_per_room: 8,
        }
    }
}

impl std::fmt::Display for ServerCapacity {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "CPU={}코어, RAM={}MB(가용={}MB) → MaxRooms={}, MaxPlayersPerRoom={}",
            self.cpu_cores,
            self.total_memory_mb,
            self.available_memory_mb,
            self.max_rooms,
            self.max_players_per_room
        )
    }
}
