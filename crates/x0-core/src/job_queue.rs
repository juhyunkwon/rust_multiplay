//! 쌓인 작업을 루프 하나에서 순서대로 실행

use std::future::Future;
use std::pin::Pin;
use tokio::sync::mpsc;

pub type Job = Pin<Box<dyn Future<Output = ()> + Send>>;

pub struct JobQueue {
    tx: mpsc::UnboundedSender<Job>,
}

impl JobQueue {
    pub fn new() -> (Self, JobQueueRunner) {
        let (tx, rx) = mpsc::unbounded_channel();
        (Self { tx }, JobQueueRunner { rx })
    }

    pub fn enqueue<F>(&self, job: F)
    where
        F: Future<Output = ()> + Send + 'static,
    {
        let _ = self.tx.send(Box::pin(job));
    }
}

pub struct JobQueueRunner {
    rx: mpsc::UnboundedReceiver<Job>,
}

impl JobQueueRunner {
    pub async fn run(mut self) {
        while let Some(job) = self.rx.recv().await {
            job.await;
        }
    }
}

impl Default for JobQueue {
    fn default() -> Self {
        Self::new().0
    }
}
