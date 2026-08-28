//! Custody reporter — streams custody records to a custody server.

use drone_core::custody::CustodyRecord;

pub struct CustodyReporter {
    queue: tokio::sync::mpsc::UnboundedSender<CustodyRecord>,
}

impl CustodyReporter {
    pub fn new() -> (Self, tokio::sync::mpsc::UnboundedReceiver<CustodyRecord>) {
        let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
        (Self { queue: tx }, rx)
    }

    pub fn report(&self, record: CustodyRecord) {
        let _ = self.queue.send(record);
    }
}
