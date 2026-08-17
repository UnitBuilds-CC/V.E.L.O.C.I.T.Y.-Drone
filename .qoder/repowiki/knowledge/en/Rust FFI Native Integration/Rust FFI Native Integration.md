---
kind: native_integration
name: Rust FFI Native Integration
category: native_integration
scope:
    - 'Drone.Native/**'
source_files:
    - Drone.Native/Drone.Native.csproj
    - Drone.Native/NativeBindings.cs
    - Drone.Native/DeltaEngine.cs
    - Drone.Native/DeltaFrameSerializer.cs
    - Drone.Native/WebpCompressor.cs
    - Drone.Native/src/lib.rs
    - Drone.Native/Cargo.toml
---

Drone.Native provides high-performance native code via Rust FFI (Foreign Function Interface), exposing performance-critical algorithms to C# through P/Invoke bindings. The native code handles delta frame serialization and WebP image compression.

**Architecture:**

```
Drone.Native/
├── src/
│   ├── lib.rs              # Rust library entry point
│   ├── delta.rs            # Delta frame serialization
│   └── webp.rs             # WebP compression
├── Cargo.toml              # Rust dependencies
├── NativeBindings.cs       # C# P/Invoke declarations
├── DeltaEngine.cs          # C# wrapper for delta operations
├── DeltaFrameSerializer.cs # C# wrapper for serialization
├── WebpCompressor.cs       # C# wrapper for compression
├── velocity_delta.dll      # Compiled native library (Windows)
└── velocity_v2_ffi.dll     # Compiled native library (Windows)
```

**Rust Crates (Cargo.toml):**
- `serde`, `serde_json` — Serialization
- `webp` — WebP image encoding
- `image` — Image processing
- Build target: `cdylib` (C-compatible dynamic library)

**C# Bindings (NativeBindings.cs):**

```csharp
internal static class NativeBindings
{
    [DllImport("velocity_delta")]
    public static extern IntPtr delta_engine_create();
    
    [DllImport("velocity_delta")]
    public static extern void delta_engine_destroy(IntPtr engine);
    
    [DllImport("velocity_delta")]
    public static extern int delta_serialize(
        IntPtr engine,
        IntPtr frameData,
        int frameSize,
        IntPtr outputBuffer,
        int outputBufferSize);
    
    [DllImport("velocity_v2_ffi")]
    public static extern int webp_compress(
        IntPtr imageData,
        int width,
        int height,
        int quality,
        IntPtr outputBuffer,
        int outputBufferSize);
}
```

**C# Wrapper Classes:**

1. **DeltaEngine** — High-level delta operations:
   - `CreateEngine()` — Initialize native engine
   - `ProcessFrame(byte[] frame)` — Process frame and return delta
   - `Dispose()` — Cleanup native resources

2. **DeltaFrameSerializer** — Frame serialization:
   - `Serialize(byte[] currentFrame, byte[] previousFrame)` — Compute delta
   - Returns compressed delta between frames
   - Uses native Rust implementation for speed

3. **WebpCompressor** — Image compression:
   - `Compress(byte[] imageData, int width, int height, int quality)` — Compress to WebP
   - Quality: 0-100 (higher = better quality, larger file)
   - Returns WebP-encoded bytes

**Build Configuration:**

- **Skip Rust Build:** Use `/p:SkipRust=true` to bypass Rust compilation
- **Cargo Build:** `cargo build --release` produces DLLs
- **Output:** `velocity_delta.dll`, `velocity_v2_ffi.dll`
- **Platform:** Windows x64 (primary), Linux x64 (Docker)

**Memory Management:**
- Native allocations tracked via opaque pointers (`IntPtr`)
- C# wrappers implement `IDisposable` for cleanup
- `delta_engine_destroy()` frees native memory
- Buffer sizes validated before crossing FFI boundary

**Performance Characteristics:**
- Delta serialization: ~10x faster than pure C# implementation
- WebP compression: Native libwebp bindings
- Zero-copy where possible (unsafe code with pinned buffers)

**Error Handling:**
- Native functions return error codes (0 = success, negative = error)
- C# wrappers throw exceptions on error
- Native panics caught and converted to error codes

**Integration Points:**
- `DeltaScreenPipeline` in Drone.Services uses delta serialization
- Screen captures compressed via WebP before transmission
- Benchmark suite in DeltaBench tests native performance

**Development:**
- Install Rust: `winget install Rustlang.Rustup`
- Build native: `cd Drone.Native; cargo build --release`
- Test native: `cargo test`
- Skip native: `dotnet build /p:SkipRust=true`
