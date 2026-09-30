#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StepStatus {
    Pass,
    Fail,
    Skip,
}

#[derive(Debug, Clone)]
pub struct Step {
    pub name: String,
    pub status: StepStatus,
    pub detail: String,
}

#[derive(Debug, Clone)]
pub struct SequenceReport {
    pub title: String,
    pub steps: Vec<Step>,
}

impl SequenceReport {
    pub fn new(title: impl Into<String>) -> Self {
        Self {
            title: title.into(),
            steps: Vec::new(),
        }
    }

    pub fn pass(&mut self, name: impl Into<String>, detail: impl Into<String>) {
        self.steps.push(Step {
            name: name.into(),
            status: StepStatus::Pass,
            detail: detail.into(),
        });
    }

    pub fn fail(&mut self, name: impl Into<String>, detail: impl Into<String>) {
        self.steps.push(Step {
            name: name.into(),
            status: StepStatus::Fail,
            detail: detail.into(),
        });
    }

    pub fn skip(&mut self, name: impl Into<String>, detail: impl Into<String>) {
        self.steps.push(Step {
            name: name.into(),
            status: StepStatus::Skip,
            detail: detail.into(),
        });
    }

    pub fn ok(&self) -> bool {
        self.steps.iter().all(|s| s.status != StepStatus::Fail)
    }

    pub fn passed(&self) -> usize {
        self.steps
            .iter()
            .filter(|s| s.status == StepStatus::Pass)
            .count()
    }

    pub fn failed(&self) -> usize {
        self.steps
            .iter()
            .filter(|s| s.status == StepStatus::Fail)
            .count()
    }
}
