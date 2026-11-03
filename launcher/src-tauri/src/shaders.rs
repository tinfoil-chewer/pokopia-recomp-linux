// SPDX-License-Identifier: GPL-3.0-or-later
//! First-run shader setup: builds `shader_cache/spirv` next to pkp.exe from the user's own romfs and main.
//!
//! Two existing tools do the work, both shipped in the launcher's `tools/` folder:
//!   1. `tools/shaders/extract_corpus.py` (Python 3 with the `lz4` package) finds every GLSLC output in the romfs and in
//!      exefs/main and writes the distinct shader blocks to `shader_cache/corpus` (code/, ctrl/, glslc/, unique.tsv);
//!   2. `tools/spirv_harness/spirv_harness.exe` (self-contained .NET publish) translates them to `<hash>.spv` plus the
//!      `results.tsv` rows the runtime reads.
//!
//! Resumable: the full block list is kept as `corpus/unique_all.tsv`; translation runs in chunks (the harness reads
//! `corpus/unique.tsv`, rewritten per chunk) into `shader_cache/stage`, and a chunk counts only once its `.spv` files are
//! moved into `spirv/` and its rows appended to `spirv/results.tsv`. A block with a row there is never translated again.
//! `shader_cache/setup.json` records what is done. The tools run at below-normal priority.
//! No crate-local imports, so `examples/shader_setup.rs` can build this file alone.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::time::Duration;

/// Folder (in the game folder) the runtime opens by default; also what setup writes as `[nvn] shader_cache`.
pub const SPIRV_REL: &str = "shader_cache/spirv";
const CHUNK: usize = 1000;

#[derive(Serialize, Deserialize, Clone, Default)]
#[serde(default)]
pub struct Marker {
    /// romfs the corpus was extracted from
    pub romfs: String,
    pub extracted: bool,
    pub blocks: usize,
    pub complete: bool,
}

#[derive(Serialize, Clone, Default)]
pub struct Progress {
    /// "idle", "running", "done", "error", "cancelled"
    pub state: String,
    /// "check", "extract", "translate", "finish"
    pub phase: String,
    pub done: u64,
    pub total: u64,
    pub message: String,
}

pub struct Tools {
    pub python: PathBuf,
    pub extract: PathBuf,
    pub harness: PathBuf,
}

pub struct Job {
    pub game_dir: PathBuf,
    pub romfs: PathBuf,
    pub main: PathBuf,
    pub jobs: usize,
    /// Test runs: translate at most this many blocks (0 = all); the setup is then never marked complete.
    pub limit: usize,
}

pub fn cache_dir(game_dir: &Path) -> PathBuf {
    game_dir.join("shader_cache")
}

pub fn read_marker(game_dir: &Path) -> Marker {
    fs::read_to_string(cache_dir(game_dir).join("setup.json")).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
}

fn write_marker(game_dir: &Path, m: &Marker) -> Result<(), String> {
    let p = cache_dir(game_dir).join("setup.json");
    fs::write(&p, serde_json::to_string_pretty(m).unwrap_or_default()).map_err(|e| format!("cannot write {}: {}", p.display(), e))
}

/// True when a finished setup's cache is in place.
pub fn is_complete(game_dir: &Path) -> bool {
    read_marker(game_dir).complete && game_dir.join(SPIRV_REL).join("results.tsv").is_file()
}

fn quiet(cmd: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS (the tools' own child processes inherit it)
        cmd.creation_flags(0x0800_0000 | 0x0000_4000);
    }
}

fn python_has_lz4(py: &Path) -> bool {
    let mut c = Command::new(py);
    c.args(["-I", "-c", "import lz4.block"]).stdout(Stdio::null()).stderr(Stdio::null());
    quiet(&mut c);
    c.status().map(|s| s.success()).unwrap_or(false)
}

fn on_path(exe: &str) -> Vec<PathBuf> {
    std::env::var_os("PATH").map(|p| std::env::split_paths(&p).map(|d| d.join(exe)).filter(|f| f.is_file()).collect()).unwrap_or_default()
}

/// Finds the tools in `tools_dir` (the launcher's `tools/` folder). Python: `tools/python/python.exe`, else the first
/// python.exe on PATH that has `lz4`. Every missing piece is named in the error.
pub fn find_tools(tools_dir: &Path) -> Result<Tools, String> {
    let mut missing = Vec::new();
    let harness = tools_dir.join("spirv_harness").join("spirv_harness.exe");
    if !harness.is_file() {
        missing.push(format!("the shader translator ({})", harness.display()));
    }
    let extract = tools_dir.join("shaders").join("extract_corpus.py");
    if !extract.is_file() {
        missing.push(format!("the shader extractor ({})", extract.display()));
    }
    let bundled = tools_dir.join("python").join("python.exe");
    let mut candidates = vec![bundled.clone()];
    candidates.extend(on_path("python.exe"));
    let python = candidates.into_iter().filter(|p| p.is_file()).find(|p| python_has_lz4(p));
    if python.is_none() {
        missing.push(format!(
            "Python 3 with the lz4 package ({}, or python.exe on PATH with `pip install lz4`)",
            bundled.display()
        ));
    }
    if !missing.is_empty() {
        return Err(format!("Shader setup cannot run, missing: {}. Re-extract the launcher's release folder so tools/ is complete.", missing.join("; ")));
    }
    Ok(Tools { python: python.unwrap(), extract, harness })
}

/// Linux: the release's extractor from `tools_dir` (the game folder's tools/), Python and the translator from the AppImage
/// (`lt`). The translator there is a native build from source with the current fixes, server GC set in its runtimeconfig.
#[cfg(unix)]
pub fn find_tools_linux(tools_dir: &Path, lt: &Path) -> Result<Tools, String> {
    let python = crate::linux::python(lt);
    let extract = tools_dir.join("shaders").join("extract_corpus.py");
    let harness = crate::linux::harness(lt);
    let missing: Vec<String> = [&python, &extract, &harness].iter().filter(|p| !p.is_file()).map(|p| p.display().to_string()).collect();
    if !missing.is_empty() {
        return Err(format!("Shader setup cannot run, missing: {}. Install the game files again (Game files page).", missing.join("; ")));
    }
    Ok(Tools { python, extract, harness })
}

/// Runs a tool, handing each stdout line to `line`; kills it when `cancel` is set. Ok(true) = exit 0, Ok(false) = cancelled.
fn run_tool(mut cmd: Command, cancel: &AtomicBool, mut line: impl FnMut(&str)) -> Result<bool, String> {
    quiet(&mut cmd);
    let name = cmd.get_program().to_string_lossy().to_string();
    let mut child: Child = cmd.stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().map_err(|e| format!("could not start {}: {}", name, e))?;
    let (tx, rx) = mpsc::channel::<String>();
    let out = child.stdout.take().unwrap();
    let tx2 = tx.clone();
    std::thread::spawn(move || BufReader::new(out).lines().map_while(Result::ok).for_each(|l| drop(tx2.send(l))));
    let err = child.stderr.take().unwrap();
    let (etx, erx) = mpsc::channel::<String>();
    std::thread::spawn(move || {
        let mut tail: Vec<String> = Vec::new();
        for l in BufReader::new(err).lines().map_while(Result::ok) {
            tail.push(l);
            if tail.len() > 12 {
                tail.remove(0);
            }
        }
        let _ = etx.send(tail.join("\n"));
    });
    drop(tx);
    loop {
        match rx.recv_timeout(Duration::from_millis(200)) {
            Ok(l) => line(&l),
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // stdout closed: the process is ending
                let st = child.wait().map_err(|e| e.to_string())?;
                if cancel.load(Ordering::SeqCst) {
                    return Ok(false);
                }
                if st.success() {
                    return Ok(true);
                }
                let tail = erx.recv_timeout(Duration::from_secs(5)).unwrap_or_default();
                return Err(format!("{} failed ({}). {}", name, st, tail.trim()));
            }
        }
        if cancel.load(Ordering::SeqCst) {
            let _ = child.kill();
            let _ = child.wait();
            return Ok(false);
        }
    }
}

/// Header + rows of a tsv file (rows without the trailing newline).
fn read_tsv(p: &Path) -> Result<(String, Vec<String>), String> {
    let text = fs::read_to_string(p).map_err(|e| format!("cannot read {}: {}", p.display(), e))?;
    let mut it = text.lines();
    let header = it.next().unwrap_or("").to_string();
    Ok((header, it.filter(|l| !l.is_empty()).map(|l| l.to_string()).collect()))
}

fn first_col(row: &str) -> &str {
    row.split('\t').next().unwrap_or("")
}

/// The whole setup. `report` gets every progress change. Ok(true) = finished (or the limit reached), Ok(false) = cancelled.
pub fn run(job: &Job, tools: &Tools, cancel: &AtomicBool, mut report: impl FnMut(Progress)) -> Result<bool, String> {
    let cache = cache_dir(&job.game_dir);
    let corpus = cache.join("corpus");
    let stage = cache.join("stage");
    let spirv = job.game_dir.join(SPIRV_REL);
    fs::create_dir_all(&spirv).map_err(|e| format!("cannot create {}: {}", spirv.display(), e))?;
    let romfs_s = job.romfs.to_string_lossy().replace('\\', "/");
    let mut marker = read_marker(&job.game_dir);
    if marker.romfs != romfs_s {
        marker = Marker { romfs: romfs_s.clone(), ..Default::default() };
    }
    marker.complete = false;
    write_marker(&job.game_dir, &marker)?;
    let jobs = job.jobs.max(1).to_string();
    let p = |phase: &str, done: u64, total: u64, message: String| Progress { state: "running".into(), phase: phase.into(), done, total, message };

    // 1. extract (skipped when this romfs was already extracted)
    let all = corpus.join("unique_all.tsv");
    if !(marker.extracted && all.is_file()) {
        report(p("extract", 0, 0, "Finding the game's shaders in your game files...".into()));
        let _ = fs::remove_file(corpus.join("unique.tsv"));
        let mut cmd = Command::new(&tools.python);
        cmd.arg("-I").arg(&tools.extract).arg("--romfs").arg(&job.romfs).arg("--main").arg(&job.main).arg("--out").arg(&corpus);
        cmd.args(["--jobs", &jobs, "--progress"]);
        let ok = run_tool(cmd, cancel, |l| {
            if let Some(rest) = l.strip_prefix("scan: ") {
                let mut it = rest.split_whitespace().filter_map(|x| x.parse::<u64>().ok());
                if let (Some(d), Some(t)) = (it.next(), it.next()) {
                    report(p("extract", d, t, format!("Scanning game files: {} of {}", d, t)));
                }
            }
        })?;
        if !ok {
            return Ok(false);
        }
        fs::rename(corpus.join("unique.tsv"), &all).map_err(|e| format!("the extractor wrote no block list: {}", e))?;
        marker.extracted = true;
        write_marker(&job.game_dir, &marker)?;
    }

    // 2. translate what spirv/results.tsv does not list yet
    let (uheader, urows) = read_tsv(&all)?;
    marker.blocks = urows.len();
    write_marker(&job.game_dir, &marker)?;
    let results = spirv.join("results.tsv");
    let done: HashSet<String> = if results.is_file() { read_tsv(&results)?.1.iter().map(|r| first_col(r).to_string()).collect() } else { HashSet::new() };
    let mut todo: Vec<&String> = urows.iter().filter(|r| !done.contains(first_col(r))).collect();
    let limited = job.limit > 0 && todo.len() > job.limit;
    if job.limit > 0 {
        todo.truncate(job.limit);
    }
    let total = urows.len() as u64;
    let mut finished = (urows.len() - urows.iter().filter(|r| !done.contains(first_col(r))).count()) as u64;
    report(p("translate", finished, total, format!("Translating shaders: {} of {}", finished, total)));
    for chunk in todo.chunks(CHUNK) {
        let mut f = fs::File::create(corpus.join("unique.tsv")).map_err(|e| e.to_string())?;
        writeln!(f, "{}", uheader).map_err(|e| e.to_string())?;
        for r in chunk {
            writeln!(f, "{}", r).map_err(|e| e.to_string())?;
        }
        drop(f);
        let _ = fs::remove_dir_all(&stage);
        let mut cmd = Command::new(&tools.harness);
        cmd.arg("--batch").arg(&corpus).arg("--out").arg(&stage).args(["--jobs", &jobs, "--no-val"]);
        let base = finished;
        let ok = run_tool(cmd, cancel, |l| {
            // "  <n>/<count>  <s> s" every 500 blocks
            if let Some(n) = l.trim().split('/').next().and_then(|x| x.parse::<u64>().ok()) {
                report(p("translate", base + n, total, format!("Translating shaders: {} of {}", base + n, total)));
            }
        })?;
        if !ok {
            return Ok(false);
        }
        // commit the chunk: .spv files first, then the rows that make them count
        let (rheader, rrows) = read_tsv(&stage.join("results.tsv"))?;
        for r in &rrows {
            let h = first_col(r);
            let src = stage.join(format!("{}.spv", h));
            if src.is_file() {
                let dst = spirv.join(format!("{}.spv", h));
                let _ = fs::remove_file(&dst);
                fs::rename(&src, &dst).map_err(|e| format!("cannot move {}: {}", src.display(), e))?;
            }
        }
        let new_file = !results.is_file();
        let mut out = fs::OpenOptions::new().create(true).append(true).open(&results).map_err(|e| format!("cannot write {}: {}", results.display(), e))?;
        let mut text = String::new();
        if new_file {
            text.push_str(&rheader);
            text.push('\n');
        }
        for r in &rrows {
            text.push_str(r);
            text.push('\n');
        }
        out.write_all(text.as_bytes()).map_err(|e| format!("cannot write {}: {}", results.display(), e))?;
        finished += chunk.len() as u64;
        report(p("translate", finished, total, format!("Translating shaders: {} of {}", finished, total)));
    }
    let _ = fs::remove_dir_all(&stage);
    let _ = fs::remove_file(corpus.join("unique.tsv"));
    if limited {
        return Ok(true);
    }

    // 3. done: the corpus is only needed to translate, drop it (several GB)
    report(p("finish", total, total, "Cleaning up...".into()));
    let _ = fs::remove_dir_all(&corpus);
    marker.extracted = false;
    marker.complete = true;
    write_marker(&job.game_dir, &marker)?;
    Ok(true)
}
