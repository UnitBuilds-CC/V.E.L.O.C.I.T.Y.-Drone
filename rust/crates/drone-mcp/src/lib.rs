//! Drone MCP — MCP server with NMCP shared memory and WebSocket transports.
//!
//! Provides:
//! - Shared memory IPC (atomic state machine, 100μs polling)
//! - JSON-RPC over WebSocket (remote access)
//! - Tool registry with dynamic registration

pub mod server;
pub mod tool_registry;

pub use server::McpServer;
pub use server::McpServerRef;
pub use tool_registry::ToolRegistry;
