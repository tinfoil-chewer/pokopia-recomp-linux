// SPDX-License-Identifier: GPL-3.0-or-later
//! Starts pkp.exe, watches it, and reports how it ended.

use serde::Serialize;
use std::path::Path;
use std::process::Command;
use std::sync::Mutex;
use std::time::{Duration, SystemTime};
use tauri::{AppHandle, Emitter, Manager};

/// Files the launcher may open for the user, by name, inside the game folder.
pub const REPORT_FILES: [&str; 4] = ["pkp_crash.txt", "pkp_freeze.txt", "pkp_fatal.txt", "pkp.log"];

#[derive(Serialize, Clone, Default)]
pub struct ReportFile {
    pub name: String,
    /// Written since this run started.
    pub fresh: bool,
}

#[derive(Serialize, Clone)]
pub struct Status {
    /// "idle", "running" or "exited"
    pub state: String,
    pub code: Option<i32>,
    pub crashed: bool,
    pub files: Vec<ReportFile>,
    pub message: String,
}

impl Default for Status {
    fn default() -> Self {
        Status { state: "idle".into(), code: None, crashed: false, files: vec![], message: String::new() }
    }
}

#[derive(Default)]
pub struct PlayState(pub Mutex<Status>);

pub(crate) fn set_status(app: &AppHandle, s: Status) {
    *app.state::<PlayState>().0.lock().unwrap() = s.clone();
    let _ = app.emit("game-status", s);
}

fn report_files(dir: &Path, started: SystemTime) -> Vec<ReportFile> {
    REPORT_FILES
        .iter()
        .filter_map(|n| {
            let md = std::fs::metadata(dir.join(n)).ok()?;
            let fresh = md.modified().map(|m| m + Duration::from_secs(2) >= started).unwrap_or(false);
            Some(ReportFile { name: n.to_string(), fresh })
        })
        .collect()
}

pub fn start(app: &AppHandle, game_dir: &Path, minimize: bool) -> Result<(), String> {
    {
        let st = app.state::<PlayState>();
        if st.0.lock().unwrap().state == "running" {
            return Err("The game is already running.".into());
        }
    }
    let exe = game_dir.join("pkp.exe");
    if !exe.is_file() {
        return Err(format!("pkp.exe was not found in {}. Put the launcher next to pkp.exe or set the game folder in Settings.", crate::dump::fwd(game_dir)));
    }

    let mut cmd = Command::new(&exe);
    // every path (exefs, romfs, shader cache) comes from pkp.ini next to pkp.exe
    cmd.current_dir(game_dir).args(["--log", "pkp.log"]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW: no stray console if pkp.exe is a console build
    }
    let started = SystemTime::now();
    let mut child = cmd.spawn().map_err(|e| format!("Could not start pkp.exe: {}", e))?;

    set_status(app, Status { state: "running".into(), message: format!("pkp.exe started (process {})", child.id()), ..Default::default() });
    if minimize {
        if let Some(w) = app.get_webview_window("main") {
            let _ = w.minimize();
        }
    }

    let app2 = app.clone();
    let dir = game_dir.to_path_buf();
    std::thread::spawn(move || {
        let result = child.wait();
        let (code, wait_err) = match result {
            Ok(s) => (s.code(), None),
            Err(e) => (None, Some(e.to_string())),
        };
        let files = report_files(&dir, started);
        let crash_file = files.iter().any(|f| f.fresh && f.name != "pkp.log");
        let crashed = crash_file || code.map_or(true, |c| c != 0);
        let message = match (&wait_err, code) {
            (Some(e), _) => format!("Lost track of pkp.exe: {}", e),
            (None, Some(0)) => "pkp.exe exited normally.".to_string(),
            (None, Some(c)) => format!("pkp.exe exited with code {} (0x{:08X}).", c, c as u32),
            (None, None) => "pkp.exe was ended by the system.".to_string(),
        };
        set_status(&app2, Status { state: "exited".into(), code, crashed, files, message });
        if minimize {
            if let Some(w) = app2.get_webview_window("main") {
                let _ = w.unminimize();
                let _ = w.set_focus();
            }
        }
    });
    Ok(())
}

pub fn open_with_shell(path: &Path) -> Result<(), String> {
    #[cfg(unix)]
    return crate::linux::open(path);
    // explorer opens a file in its default app and a folder in a window
    #[cfg(not(unix))]
    return Command::new("explorer.exe").arg(path).spawn().map(|_| ()).map_err(|e| format!("Could not open {}: {}", path.display(), e));
}
