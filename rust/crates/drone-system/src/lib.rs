//! Drone System — platform abstraction traits and implementations.
//!
//! Provides cross-platform interfaces for:
//! - Screen capture
//! - Input simulation (keyboard, mouse)
//! - Process management
//! - Clipboard
//! - Window management

pub mod traits;

// Platform-specific implementations
#[cfg(target_os = "windows")]
pub mod windows;

#[cfg(target_os = "linux")]
pub mod linux;

#[cfg(target_os = "macos")]
pub mod macos;

pub use traits::*;
