// SPDX-License-Identifier: GPL-3.0-or-later
//! First-run game build (release model b): the release ships no game code, so pkp.exe is built here, on the player's PC,
//! from their own dump. The work is done by `tools/build/pkp_build.py` (bundled Python, lifter and Zig; see that file):
//! verify the ExeFS by hash, lift the four modules, compile them, link pkp.exe against pkp_rt.dll (the runtime).
//!
//! Resumable: the script keeps finished modules and objects in `<game>/build`; Pause writes `<game>/build/cancel`, which
//! stops it and its compilers. `<game>/build/done.json` plus pkp.exe means the build is finished.
//! No crate-local imports, so `examples/first_run.rs` can build this file alone.

use std::fs;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

pub struct Tools {
    pub python: PathBuf,
    pub script: PathBuf,
    /// Linux: `_build --script <release pkp_build.py> --zig <Linux Zig>` for pkp_linux.py, which runs the release's script
    /// unchanged with the Linux build of the same Zig. Empty on Windows.
    pub pre_args: Vec<std::ffi::OsString>,
}

pub struct Job {
    pub game_dir: PathBuf,
    pub exefs: PathBuf,
    pub jobs: usize,
}

/// One progress line of the script: phase ("verify", "lift", "compile", "link", "finish"), done, total, text.
pub struct Tick {
    pub phase: String,
    pub done: u64,
    pub total: u64,
    pub message: String,
}

/// The package builds pkp.exe itself (model b) when it carries the build tools.
pub fn is_package_b(tools_dir: &Path) -> bool {
    tools_dir.join("build").join("pkp_build.py").is_file()
}

/// pkp.exe was built from this release's runtime and is in place.
pub fn is_built(game_dir: &Path) -> bool {
    game_dir.join("pkp.exe").is_file() && game_dir.join("build").join("done.json").is_file()
}

pub fn find_tools(tools_dir: &Path) -> Result<Tools, String> {
    let python = tools_dir.join("python").join("python.exe");
    let script = tools_dir.join("build").join("pkp_build.py");
    let mut missing = Vec::new();
    if !python.is_file() {
        missing.push(python.display().to_string());
    }
    if !script.is_file() {
        missing.push(script.display().to_string());
    }
    for p in ["build/zig/zig.exe", "build/sdk/pkp_rt.lib", "build/recomp/nxrecomp/__main__.py"] {
        if !tools_dir.join(p).is_file() {
            missing.push(tools_dir.join(p).display().to_string());
        }
    }
    if !missing.is_empty() {
        return Err(format!("The game build cannot run, missing: {}. Extract the release zip again so tools/ is complete.", missing.join("; ")));
    }
    Ok(Tools { python, script, pre_args: Vec::new() })
}

/// Linux: the release's build files in `tools_dir` (the game folder's tools/), Python and Zig from the AppImage (`lt`).
#[cfg(unix)]
pub fn find_tools_linux(tools_dir: &Path, lt: &Path) -> Result<Tools, String> {
    let python = crate::linux::python(lt);
    let script = crate::linux::script(lt, "pkp_linux.py");
    let zig = crate::linux::zig(lt);
    let release_script = tools_dir.join("build").join("pkp_build.py");
    let mut missing = Vec::new();
    for p in [&python, &script, &zig, &release_script, &tools_dir.join("build/sdk/pkp_rt.lib"), &tools_dir.join("build/recomp/nxrecomp/__main__.py")] {
        if !p.is_file() {
            missing.push(p.display().to_string());
        }
    }
    if !missing.is_empty() {
        return Err(format!("The game build cannot run, missing: {}. Install the game files again (Game files page).", missing.join("; ")));
    }
    let pre_args = vec!["_build".into(), "--script".into(), release_script.into_os_string(), "--zig".into(), zig.into_os_string()];
    Ok(Tools { python, script, pre_args })
}

fn quiet(cmd: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS (the compilers it starts inherit it)
        cmd.creation_flags(0x0800_0000 | 0x0000_4000);
    }
}

/// Runs the build. Ok(true) = pkp.exe built, Ok(false) = paused, Err = what went wrong, in the script's words.
pub fn run(job: &Job, tools: &Tools, cancel: &AtomicBool, mut report: impl FnMut(Tick)) -> Result<bool, String> {
    let build = job.game_dir.join("build");
    fs::create_dir_all(&build).map_err(|e| format!("cannot create {}: {}", build.display(), e))?;
    let cancel_file = build.join("cancel");
    let _ = fs::remove_file(&cancel_file);
    let mut cmd = Command::new(&tools.python);
    cmd.arg("-I").arg(&tools.script).args(&tools.pre_args).arg("--exefs").arg(&job.exefs).arg("--game").arg(&job.game_dir);
    cmd.args(["--jobs", &job.jobs.max(1).to_string()]);
    quiet(&mut cmd);
    let mut child = cmd.stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().map_err(|e| format!("could not start the game build ({}): {}", tools.python.display(), e))?;
    let out = child.stdout.take().unwrap();
    let err = child.stderr.take().unwrap();
    let (tx, rx) = std::sync::mpsc::channel::<String>();
    std::thread::spawn(move || BufReader::new(out).lines().map_while(Result::ok).for_each(|l| drop(tx.send(l))));
    let err_tail = std::thread::spawn(move || {
        let mut tail: Vec<String> = Vec::new();
        for l in BufReader::new(err).lines().map_while(Result::ok) {
            tail.push(l);
            if tail.len() > 12 {
                tail.remove(0);
            }
        }
        tail.join("\n")
    });
    let mut error: Option<String> = None;
    let mut paused = false;
    let mut asked = false;
    loop {
        if cancel.load(Ordering::SeqCst) && !asked {
            // the script stops its compilers and exits 2; nothing is killed from here
            let _ = fs::write(&cancel_file, "pause\n");
            asked = true;
        }
        match rx.recv_timeout(Duration::from_millis(200)) {
            Ok(l) => {
                if let Some(rest) = l.strip_prefix("@progress ") {
                    let mut it = rest.splitn(4, ' ');
                    let phase = it.next().unwrap_or("").to_string();
                    let done = it.next().and_then(|x| x.parse().ok()).unwrap_or(0);
                    let total = it.next().and_then(|x| x.parse().ok()).unwrap_or(0);
                    let message = it.next().unwrap_or("").to_string();
                    report(Tick { phase, done, total, message });
                } else if let Some(rest) = l.strip_prefix("@error ") {
                    error = Some(rest.replace("\\n", "\n"));
                } else if l.starts_with("@cancelled") {
                    paused = true;
                }
            }
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    let st = child.wait().map_err(|e| e.to_string())?;
    let _ = fs::remove_file(&cancel_file);
    if paused || (asked && st.code() == Some(2)) {
        return Ok(false);
    }
    if st.success() {
        return Ok(true);
    }
    let tail = err_tail.join().unwrap_or_default();
    Err(error.unwrap_or_else(|| format!("The game build failed ({}). {}", st, tail.trim())))
}
