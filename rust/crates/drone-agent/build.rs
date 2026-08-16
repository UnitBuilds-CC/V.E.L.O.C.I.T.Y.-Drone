use embed_manifest::{embed_manifest, new_manifest};

fn main() {
    // Embed Windows application manifest with asInvoker (no UAC prompt)
    // plus DPI awareness and Windows 10/11 compatibility.
    if std::env::var_os("CARGO_CFG_WINDOWS").is_some() {
        embed_manifest(new_manifest("VelocityDrone.Agent"))
            .expect("unable to embed manifest file");
    }
    println!("cargo:rerun-if-changed=build.rs");
}
