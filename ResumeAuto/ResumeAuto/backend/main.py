import customtkinter as ctk
import tkinter as tk
from tkinter import filedialog, messagebox
import asyncio
import websockets
import threading
import queue
import json
import uuid
import os
import re
import subprocess
import tempfile
import webbrowser
import sqlite3
import csv
import shutil
import logging
import time
from logging.handlers import RotatingFileHandler
from datetime import datetime, date, timedelta
from typing import List, Dict, Optional, Any
from dataclasses import dataclass, asdict

# ============================================================================
# CONSTANTS
# ============================================================================
WS_PORT = 12345
UI_POLL_INTERVAL_MS = 100
WORD_MACRO_TIMEOUT_SECONDS = 30
URL_DISPLAY_LENGTH = 40
FILENAME_DISPLAY_LENGTH = 25
MAX_RETRY_ROUNDS_DEFAULT = 2
JOBS_BASE_DIR = "jobs_data"
BACKUP_DIR = "backups"
LOG_FILE = "automator.log"
DB_FILE = "jobs.db"
SETTINGS_FILE = "app_settings.json"
PROFILES_FILE = "profiles.json"
MAX_BACKUP_DAYS = 7
MAX_LOG_BYTES = 5 * 1024 * 1024  # 5 MB
LOG_BACKUP_COUNT = 3
WS_TIMEOUT_SECONDS = 60

# Improved URL regex - handles more valid URL formats
URL_REGEX = re.compile(
    r'^(https?://)'
    r'(?:(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+[A-Z]{2,63}|'
    r'localhost|'
    r'\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})'
    r'(?::\d+)?'
    r'(?:[/?#][^\s]*)?$',
    re.IGNORECASE
)

# ============================================================================
# LOGGING SETUP (with rotation)
# ============================================================================
logger = logging.getLogger("JobAutomator")
logger.setLevel(logging.INFO)

# Rotating file handler
file_handler = RotatingFileHandler(
    LOG_FILE, maxBytes=MAX_LOG_BYTES, backupCount=LOG_BACKUP_COUNT, encoding='utf-8'
)
file_handler.setFormatter(logging.Formatter('%(asctime)s [%(levelname)s] %(message)s'))
logger.addHandler(file_handler)

# Console handler
console_handler = logging.StreamHandler()
console_handler.setFormatter(logging.Formatter('%(asctime)s [%(levelname)s] %(message)s'))
logger.addHandler(console_handler)

# ============================================================================
# DATA CLASSES
# ============================================================================
@dataclass
class Job:
    job_id: str
    url: str
    status: str
    profile_name: str
    date: str
    filename1: str = ""
    filename2: str = ""
    created_at: str = ""

# ============================================================================
# DATABASE MANAGER
# ============================================================================
class JobDatabase:
    def __init__(self, db_path: str = DB_FILE):
        self.db_path = db_path
        self.lock = threading.Lock()
        self._init_db()

    def _init_db(self):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS jobs (
                        job_id TEXT PRIMARY KEY,
                        profile_name TEXT NOT NULL,
                        date TEXT NOT NULL,
                        url TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'Pending',
                        filename1 TEXT DEFAULT '',
                        filename2 TEXT DEFAULT '',
                        created_at TEXT DEFAULT CURRENT_TIMESTAMP
                    )
                """)
                conn.execute("""
                    CREATE INDEX IF NOT EXISTS idx_jobs_profile_date 
                    ON jobs(profile_name, date)
                """)
                conn.execute("""
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_jobs_unique 
                    ON jobs(profile_name, date, url)
                """)
                conn.execute("""
                    CREATE INDEX IF NOT EXISTS idx_jobs_url 
                    ON jobs(url)
                """)
                conn.commit()

    def add_job(self, job: Job) -> bool:
        """Returns False if duplicate, True if added."""
        with self.lock:
            try:
                with sqlite3.connect(self.db_path) as conn:
                    conn.execute(
                        """INSERT INTO jobs 
                           (job_id, profile_name, date, url, status, filename1, filename2, created_at)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                        (job.job_id, job.profile_name, job.date, job.url,
                         job.status, job.filename1, job.filename2,
                         job.created_at or datetime.now().isoformat())
                    )
                    conn.commit()
                    return True
            except sqlite3.IntegrityError:
                return False

    def add_jobs_batch(self, jobs: List[Job]) -> int:
        added = 0
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                for job in jobs:
                    try:
                        conn.execute(
                            """INSERT INTO jobs 
                               (job_id, profile_name, date, url, status, filename1, filename2, created_at)
                               VALUES (?, ?, ?, ?, ?, ?, ?, ?)""",
                            (job.job_id, job.profile_name, job.date, job.url,
                             job.status, job.filename1, job.filename2,
                             job.created_at or datetime.now().isoformat())
                        )
                        added += 1
                    except sqlite3.IntegrityError:
                        continue
                conn.commit()
        return added

    def update_status(self, job_id: str, status: str):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "UPDATE jobs SET status = ? WHERE job_id = ?",
                    (status, job_id)
                )
                conn.commit()

    def update_filenames(self, job_id: str, filename1: str, filename2: str):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "UPDATE jobs SET filename1 = ?, filename2 = ? WHERE job_id = ?",
                    (filename1, filename2, job_id)
                )
                conn.commit()

    def update_job(self, job: Job):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    """UPDATE jobs SET url=?, status=?, filename1=?, filename2=? 
                       WHERE job_id=?""",
                    (job.url, job.status, job.filename1, job.filename2, job.job_id)
                )
                conn.commit()

    def delete_job(self, job_id: str):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("DELETE FROM jobs WHERE job_id = ?", (job_id,))
                conn.commit()

    def delete_jobs(self, job_ids: List[str]):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.executemany("DELETE FROM jobs WHERE job_id = ?",
                                 [(jid,) for jid in job_ids])
                conn.commit()

    def delete_all_for_profile_date(self, profile_name: str, date_str: str):
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "DELETE FROM jobs WHERE profile_name = ? AND date = ?",
                    (profile_name, date_str)
                )
                conn.commit()

    def get_jobs(self, profile_name: str, date_str: str) -> List[Job]:
        """FIXED: Column order now matches Job dataclass fields."""
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    """SELECT job_id, url, status, profile_name, date, 
                              filename1, filename2, created_at
                       FROM jobs WHERE profile_name = ? AND date = ?
                       ORDER BY created_at DESC""",
                    (profile_name, date_str)
                )
                return [Job(*row) for row in cursor.fetchall()]

    def search_jobs(self, profile_name: str, date_str: str, query: str) -> List[Job]:
        """FIXED: Column order now matches Job dataclass fields."""
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                like_query = f"%{query}%"
                cursor = conn.execute(
                    """SELECT job_id, url, status, profile_name, date, 
                              filename1, filename2, created_at
                       FROM jobs 
                       WHERE profile_name = ? AND date = ? 
                       AND (url LIKE ? OR filename1 LIKE ? OR filename2 LIKE ?)
                       ORDER BY created_at DESC""",
                    (profile_name, date_str, like_query, like_query, like_query)
                )
                return [Job(*row) for row in cursor.fetchall()]

    def url_exists_anywhere(self, url: str) -> List[Dict]:
        """Check if URL exists in any date/profile. Returns list of {profile, date}."""
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    "SELECT profile_name, date FROM jobs WHERE url = ?",
                    (url,)
                )
                return [{"profile": row[0], "date": row[1]} for row in cursor.fetchall()]

    def get_statistics(self, profile_name: str, date_str: str) -> Dict[str, int]:
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    """SELECT status, COUNT(*) FROM jobs 
                       WHERE profile_name = ? AND date = ?
                       GROUP BY status""",
                    (profile_name, date_str)
                )
                stats = {row[0]: row[1] for row in cursor.fetchall()}
                stats['total'] = sum(stats.values())
                return stats

    def get_available_dates(self, profile_name: str) -> List[str]:
        with self.lock:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute(
                    """SELECT DISTINCT date FROM jobs 
                       WHERE profile_name = ? ORDER BY date DESC""",
                    (profile_name,)
                )
                return [row[0] for row in cursor.fetchall()]

    def backup(self) -> Optional[str]:
        try:
            os.makedirs(BACKUP_DIR, exist_ok=True)
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            backup_path = os.path.join(BACKUP_DIR, f"jobs_backup_{timestamp}.db")
            with self.lock:
                shutil.copy2(self.db_path, backup_path)
            self._cleanup_old_backups()
            logger.info(f"Database backed up to {backup_path}")
            return backup_path
        except Exception as e:
            logger.error(f"Backup failed: {e}")
            return None

    def _cleanup_old_backups(self):
        """Remove backups older than MAX_BACKUP_DAYS."""
        try:
            cutoff = datetime.now() - timedelta(days=MAX_BACKUP_DAYS)
            for filename in os.listdir(BACKUP_DIR):
                if not filename.startswith("jobs_backup_") or not filename.endswith(".db"):
                    continue
                filepath = os.path.join(BACKUP_DIR, filename)
                if os.path.getmtime(filepath) < cutoff.timestamp():
                    try:
                        os.remove(filepath)
                        logger.info(f"Deleted old backup: {filename}")
                    except Exception as e:
                        logger.warning(f"Failed to delete old backup {filename}: {e}")
        except Exception as e:
            logger.warning(f"Backup cleanup failed: {e}")

    def export_csv(self, profile_name: str, date_str: str, filepath: str) -> bool:
        try:
            jobs = self.get_jobs(profile_name, date_str)
            with open(filepath, 'w', newline='', encoding='utf-8') as f:
                writer = csv.writer(f)
                writer.writerow(['job_id', 'url', 'status', 'filename1', 'filename2', 'created_at'])
                for job in jobs:
                    writer.writerow([job.job_id, job.url, job.status,
                                     job.filename1, job.filename2, job.created_at])
            return True
        except Exception as e:
            logger.error(f"Export failed: {e}")
            return False

    def export_statistics_csv(self, profile_name: str, filepath: str) -> bool:
        """Export statistics for all dates for a profile."""
        try:
            dates = self.get_available_dates(profile_name)
            with open(filepath, 'w', newline='', encoding='utf-8') as f:
                writer = csv.writer(f)
                writer.writerow(['date', 'total', 'pending', 'running', 'resume_received', 'done', 'failed'])
                for d in dates:
                    stats = self.get_statistics(profile_name, d)
                    writer.writerow([
                        d,
                        stats.get('total', 0),
                        stats.get('Pending', 0),
                        stats.get('Running', 0),
                        stats.get('Resume Received', 0),
                        stats.get('Done', 0),
                        stats.get('Failed', 0)
                    ])
            return True
        except Exception as e:
            logger.error(f"Statistics export failed: {e}")
            return False

    def import_csv(self, filepath: str, profile_name: str, date_str: str) -> Dict[str, int]:
        """Import jobs from CSV with validation. Returns {imported, skipped, invalid}."""
        result = {"imported": 0, "skipped": 0, "invalid": 0}
        valid_statuses = {"Pending", "Running", "Resume Received", "Done", "Failed"}
        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                reader = csv.DictReader(f)
                jobs_to_add = []
                for row in reader:
                    url = row.get('url', '').strip()
                    if not url or not URL_REGEX.match(url):
                        result["invalid"] += 1
                        continue
                    status = row.get('status', 'Pending').strip()
                    if status not in valid_statuses:
                        status = "Pending"
                    job = Job(
                        job_id=str(uuid.uuid4())[:8],
                        url=url,
                        status=status,
                        profile_name=profile_name,
                        date=date_str,
                        filename1=row.get('filename1', '').strip(),
                        filename2=row.get('filename2', '').strip()
                    )
                    if self.add_job(job):
                        result["imported"] += 1
                    else:
                        result["skipped"] += 1
            return result
        except Exception as e:
            logger.error(f"Import failed: {e}")
            return result

# ============================================================================
# PROFILE & SETTINGS MANAGEMENT
# ============================================================================
def load_profiles() -> List[Dict]:
    if os.path.exists(PROFILES_FILE):
        try:
            with open(PROFILES_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            logger.error(f"Failed to load profiles: {e}")
    return [{"name": "Default", "prompt_path": "prompt.txt", "docm_path": "", "macro_name": ""}]

def save_profiles(profiles: List[Dict]):
    with open(PROFILES_FILE, "w", encoding="utf-8") as f:
        json.dump(profiles, f, indent=2)

def is_profile_complete(profile: Dict) -> bool:
    return bool(profile.get("prompt_path") and profile.get("docm_path") and profile.get("macro_name"))

def load_app_settings() -> Dict:
    if os.path.exists(SETTINGS_FILE):
        try:
            with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
                data.setdefault("current_profile", "Default")
                data.setdefault("generate_for_all", False)
                data.setdefault("max_retry_rounds", MAX_RETRY_ROUNDS_DEFAULT)
                data.setdefault("check_duplicates_across_dates", False)
                data.setdefault("auto_backup_on_startup", True)
                return data
        except Exception as e:
            logger.error(f"Failed to load settings: {e}")
    return {
        "current_profile": "Default",
        "generate_for_all": False,
        "max_retry_rounds": MAX_RETRY_ROUNDS_DEFAULT,
        "check_duplicates_across_dates": False,
        "auto_backup_on_startup": True
    }

def save_app_settings(settings: Dict):
    with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
        json.dump(settings, f, indent=2)

def validate_prompt_file(path: str) -> bool:
    """Validate prompt file exists and has content."""
    if not path or not os.path.exists(path):
        return False
    try:
        with open(path, "r", encoding="utf-8") as f:
            content = f.read().strip()
            return len(content) > 10  # At least some meaningful content
    except Exception as e:
        logger.error(f"Prompt validation failed: {e}")
        return False

# ============================================================================
# MACRO LOCK (prevents race condition with bridge file)
# ============================================================================
macro_lock = threading.Lock()

# ============================================================================
# POWERSHELL MACRO EXECUTION
# ============================================================================
def run_word_macro(resume_text: str, docm_path: str, macro_name: str, 
                   profile_name: str) -> bool:
    """Execute Word macro with thread safety."""
    # Acquire lock to prevent bridge file race condition
    with macro_lock:
        return _run_word_macro_internal(resume_text, docm_path, macro_name, profile_name)

def _run_word_macro_internal(resume_text: str, docm_path: str, macro_name: str, 
                             profile_name: str) -> bool:
    """Internal macro execution (called under lock)."""
    docm_path = os.path.normpath(docm_path)
    if not os.path.exists(docm_path):
        logger.error(f"[Macro:{profile_name}] DOCM file not found: {docm_path}")
        return False

    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.txt') as f:
        f.write(resume_text)
        temp_txt_path = f.name

    # Use simple bridge file name (macro_lock prevents conflicts)
    bridge_file_name = "resume_bridge_path.txt"

    ps_script = r"""
param ([string]$TempTextPath, [string]$DocmPath, [string]$MacroName, [string]$ProfileName, [string]$BridgeFileName)
$word = $null
try {
    if (-not (Test-Path $TempTextPath)) { throw "Temp text file not found." }
    
    $bridgeFile = Join-Path $env:TEMP $BridgeFileName
    $bridgeContent = $TempTextPath
    [System.IO.File]::WriteAllText($bridgeFile, $bridgeContent, [System.Text.Encoding]::UTF8)
    
    Start-Sleep -Milliseconds 500

    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0 
    $word.ScreenUpdating = $true 
    Start-Sleep -Seconds 2

    $doc = $word.Documents.Open($DocmPath)
    Start-Sleep -Seconds 2
    $doc.Repaginate()
    Start-Sleep -Seconds 1

    Write-Host "Running macro for [$ProfileName]: $MacroName"
    $word.Run($MacroName)

    Write-Host "Waiting for Word to close..."
    $startTime = Get-Date
    while ($true) {
        Start-Sleep -Milliseconds 500
        try {
            $null = $word.Name 
            $elapsed = ((Get-Date) - $startTime).TotalSeconds
            if ($elapsed -gt """ + str(WORD_MACRO_TIMEOUT_SECONDS) + r""") {
                Write-Host "FAILED: Word process did not close gracefully. Force killing..."
                
                # Force kill Word process
                $word.Quit([ref]0) 
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
                
                # Additional force kill via taskkill
                Start-Process -FilePath "taskkill" -ArgumentList "/F /IM WINWORD.EXE" -Wait -NoNewWindow
                
                exit 1 
            }
        } catch { break }
    }
    Write-Output "SUCCESS"
    exit 0
} catch {
    Write-Host "POWERSHELL ERROR: $($_.Exception.Message)"
    if ($word) {
        try { 
            $word.Quit([ref]0)
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
        } catch {}
    }
    # Force kill any remaining Word processes
    Start-Process -FilePath "taskkill" -ArgumentList "/F /IM WINWORD.EXE" -Wait -NoNewWindow -ErrorAction SilentlyContinue
    exit 1
} finally {
    if ($TempTextPath -and (Test-Path $TempTextPath)) { Remove-Item $TempTextPath -Force -ErrorAction SilentlyContinue }
    if ($bridgeFile -and (Test-Path $bridgeFile)) { Remove-Item $bridgeFile -Force -ErrorAction SilentlyContinue }
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
}
"""
    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.ps1') as f:
        f.write(ps_script)
        ps_script_path = f.name

    try:
        logger.info(f"[Macro:{profile_name}] Executing PowerShell script...")
        subprocess.run(
            ['powershell.exe', '-ExecutionPolicy', 'Bypass', '-File', ps_script_path,
             '-TempTextPath', temp_txt_path, '-DocmPath', docm_path,
             '-MacroName', macro_name, '-ProfileName', profile_name,
             '-BridgeFileName', bridge_file_name],
            capture_output=True, text=True, check=True
        )
        logger.info(f"[Macro:{profile_name}] Success")
        return True
    except subprocess.CalledProcessError as e:
        logger.error(f"[Macro:{profile_name}] Failed:")
        if e.stdout: logger.error(f"STDOUT:\n{e.stdout}")
        if e.stderr: logger.error(f"STDERR:\n{e.stderr}")
        return False
    except Exception as e:
        logger.error(f"[Macro:{profile_name}] Unexpected error: {e}")
        return False
    finally:
        if os.path.exists(ps_script_path):
            try: os.remove(ps_script_path)
            except: pass
        if os.path.exists(temp_txt_path):
            try: os.remove(temp_txt_path)
            except: pass

# ============================================================================
# WEBSOCKET SERVER LOGIC
# ============================================================================
def parse_job_description(raw_text: str) -> str:
    sections = ["description", "requirements", "qualifications", "skills", "responsibilities"]
    extracted, current_section, current_content = [], None, []
    for line in raw_text.split('\n'):
        line_lower = line.lower().strip()
        is_header = any(line_lower.startswith(sec) or line_lower.endswith(sec + ":") for sec in sections)
        if is_header and len(line) < 60:
            if current_section and current_content:
                extracted.append(f"### {current_section.strip()}\n" + "\n".join(current_content).strip())
            current_section, current_content = line.strip(), []
        elif current_section:
            current_content.append(line)
    if current_section and current_content:
        extracted.append(f"### {current_section.strip()}\n" + "\n".join(current_content).strip())
    return "\n\n".join(extracted) if extracted else raw_text[:2000] + "\n...(truncated)"


class WebSocketManager:
    """Thread-safe WebSocket manager with timeout support."""
    def __init__(self):
        self.connected_clients: set = set()
        self.clients_lock = threading.Lock()
        self.job_profile_map: Dict[str, str] = {}
        self.map_lock = threading.Lock()
        self.ws_to_ui_queue: queue.Queue = queue.Queue()
        self.ui_to_ws_queue: asyncio.Queue = None
        self.asyncio_loop: Optional[asyncio.AbstractEventLoop] = None
        # Track jobs with timestamps for timeout detection
        self.running_jobs: Dict[str, float] = {}
        self.running_jobs_lock = threading.Lock()

    def add_client(self, ws):
        with self.clients_lock:
            self.connected_clients.add(ws)
        logger.info("✅ Extension connected")
        self.ws_to_ui_queue.put(json.dumps({"type": "EXTENSION_STATUS", "status": "connected"}))

    def remove_client(self, ws):
        with self.clients_lock:
            self.connected_clients.discard(ws)
        logger.info("❌ Extension disconnected")
        self.ws_to_ui_queue.put(json.dumps({"type": "EXTENSION_STATUS", "status": "disconnected"}))

    def has_clients(self) -> bool:
        with self.clients_lock:
            return bool(self.connected_clients)

    def set_job_profile(self, job_id: str, profile_name: str):
        with self.map_lock:
            self.job_profile_map[job_id] = profile_name
        with self.running_jobs_lock:
            self.running_jobs[job_id] = time.time()

    def get_job_profile(self, job_id: str) -> str:
        with self.map_lock:
            return self.job_profile_map.get(job_id, "Default")

    def complete_job(self, job_id: str):
        """Mark job as completed (remove from running tracking)."""
        with self.map_lock:
            self.job_profile_map.pop(job_id, None)
        with self.running_jobs_lock:
            self.running_jobs.pop(job_id, None)

    def get_timed_out_jobs(self) -> List[str]:
        """Return list of job IDs that have exceeded timeout."""
        now = time.time()
        timed_out = []
        with self.running_jobs_lock:
            for job_id, start_time in list(self.running_jobs.items()):
                if now - start_time > WS_TIMEOUT_SECONDS:
                    timed_out.append(job_id)
        return timed_out

    def send_to_clients(self, message: str):
        if self.asyncio_loop and self.ui_to_ws_queue:
            self.asyncio_loop.call_soon_threadsafe(
                self.ui_to_ws_queue.put_nowait, message
            )

    async def broadcast(self):
        while True:
            message = await self.ui_to_ws_queue.get()
            with self.clients_lock:
                clients = list(self.connected_clients)
            if clients:
                await asyncio.gather(
                    *[client.send(message) for client in clients],
                    return_exceptions=True
                )

    async def handler(self, websocket, profiles_loader):
        self.add_client(websocket)
        try:
            async for message in websocket:
                try:
                    data = json.loads(message)
                    if data.get("type") == "PARSE_REQUEST":
                        raw_text = data.get("raw_text", "")
                        job_id = data.get("jobId", "")
                        logger.info(f"🔄 Parsing job data for {job_id}")

                        profile_name = self.get_job_profile(job_id)
                        profiles = profiles_loader()
                        current_p = next((p for p in profiles if p["name"] == profile_name), profiles[0])
                        prompt_text = "Act as an expert resume writer."
                        prompt_path = current_p.get("prompt_path", "")
                        
                        # Validate prompt file
                        if prompt_path and validate_prompt_file(prompt_path):
                            try:
                                with open(prompt_path, "r", encoding="utf-8") as f:
                                    prompt_text = f.read().strip()
                            except Exception as e:
                                logger.error(f"Failed to read prompt: {e}")
                        else:
                            logger.warning(f"Invalid prompt file for profile {profile_name}, using default")

                        response = {
                            "type": "PARSE_RESPONSE",
                            "jobId": job_id,
                            "prompt": prompt_text,
                            "job_description": parse_job_description(raw_text)
                        }
                        await websocket.send(json.dumps(response))
                    elif data.get("type") in ["TASK_COMPLETE", "TASK_FAILED"]:
                        self.ws_to_ui_queue.put(message)
                except json.JSONDecodeError:
                    pass
        except websockets.exceptions.ConnectionClosed:
            pass
        finally:
            self.remove_client(websocket)


def start_asyncio_loop(ws_manager: WebSocketManager, profiles_loader):
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    ws_manager.asyncio_loop = loop
    ws_manager.ui_to_ws_queue = asyncio.Queue()

    async def run_server():
        async with websockets.serve(
            lambda ws: ws_manager.handler(ws, profiles_loader),
            "localhost", WS_PORT, ping_interval=20
        ):
            logger.info(f"🚀 WebSocket Server running on ws://localhost:{WS_PORT}")
            asyncio.create_task(ws_manager.broadcast())
            await asyncio.Future()

    try:
        loop.run_until_complete(run_server())
    except Exception as e:
        logger.error(f"WebSocket server stopped: {e}")
    finally:
        loop.close()

# ============================================================================
# MODERN UI APPLICATION
# ============================================================================
class JobAutomatorApp(ctk.CTk):
    def __init__(self):
        super().__init__()
        self.title("Job to ChatGPT Automator")
        self.geometry("1100x900")
        ctk.set_appearance_mode("Dark")
        ctk.set_default_color_theme("blue")
        self.minsize(900, 700)

        # Database
        self.db = JobDatabase()
        
        # State
        self.is_running = False
        self.is_paused = False
        self.retry_round = 0
        self.jobs: Dict[str, Dict] = {}
        self.profiles = load_profiles()
        self.app_settings = load_app_settings()
        self.current_date = date.today().strftime("%Y-%m-%d")
        self.search_query = ""
        self.selected_job_ids: set = set()

        # Thread-safe WebSocket manager
        self.ws_manager = WebSocketManager()

        self.status_colors = {
            "Pending": "gray",
            "Running": "#3498db",
            "Resume Received": "#f39c12",
            "Done": "#2ecc71",
            "Failed": "#e74c3c"
        }

        # Auto-backup on startup (optional)
        if self.app_settings.get("auto_backup_on_startup", True):
            self.db.backup()

        self.build_ui()
        self.load_saved_jobs()
        self.poll_queues()
        self.check_timeouts()

        # Keyboard shortcuts
        self.bind_all_shortcuts()

        threading.Thread(
            target=start_asyncio_loop,
            args=(self.ws_manager, load_profiles),
            daemon=True
        ).start()

    def bind_all_shortcuts(self):
        """Bind keyboard shortcuts."""
        self.bind("<Control-a>", lambda e: self.select_all_jobs())
        self.bind("<Control-d>", lambda e: self.deselect_all_jobs())
        self.bind("<Delete>", lambda e: self.delete_selected_jobs())
        self.bind("<Control-r>", lambda e: self.retry_selected_jobs())
        self.bind("<Control-s>", lambda e: self.start_automation())
        self.bind("<Escape>", lambda e: self.stop_automation() if self.is_running else None)

    # ========================================================================
    # UI BUILDING
    # ========================================================================
    def build_ui(self):
        parent = ctk.CTkScrollableFrame(self, corner_radius=0)
        parent.pack(fill="both", expand=True)

        self.header = ctk.CTkLabel(parent, text="🚀 Job Automation Dashboard",
                                   font=("Arial", 24, "bold"))
        self.header.pack(pady=(20, 10))

        self.progress_label = ctk.CTkLabel(parent, text="", font=("Arial", 12))
        self.progress_label.pack(pady=(0, 5))

        self.build_stats_bar(parent)
        self.build_profile_section(parent)
        self.build_config_section(parent)
        self.build_input_section(parent)
        self.build_search_batch_section(parent)
        self.build_job_list_section(parent)
        self.build_control_section(parent)
        self.build_export_import_section(parent)

        self.refresh_profile_ui()

    def build_stats_bar(self, parent):
        stats_frame = ctk.CTkFrame(parent, corner_radius=10)
        stats_frame.pack(fill="x", padx=20, pady=5)
        
        header_row = ctk.CTkFrame(stats_frame, fg_color="transparent")
        header_row.pack(fill="x", padx=10, pady=(10, 5))
        ctk.CTkLabel(header_row, text="📊 Statistics",
                     font=("Arial", 14, "bold")).pack(side="left")
        ctk.CTkButton(header_row, text="📤 Export Stats", command=self.export_statistics,
                      fg_color="#16a085", width=100).pack(side="right")

        row = ctk.CTkFrame(stats_frame, fg_color="transparent")
        row.pack(fill="x", padx=10, pady=(0, 10))

        self.stat_labels = {}
        for status, color in self.status_colors.items():
            lbl = ctk.CTkLabel(row, text=f"{status}: 0", text_color=color,
                               font=("Arial", 12, "bold"))
            lbl.pack(side="left", padx=10)
            self.stat_labels[status] = lbl

        self.stat_labels["total"] = ctk.CTkLabel(
            row, text="Total: 0", font=("Arial", 12, "bold"))
        self.stat_labels["total"].pack(side="right", padx=10)

    def build_profile_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=5)

        selector_row = ctk.CTkFrame(frame, fg_color="transparent")
        selector_row.pack(fill="x", padx=10, pady=10)
        ctk.CTkLabel(selector_row, text="Active Profile:",
                     font=("Arial", 14, "bold"), width=120, anchor="w").pack(side="left", padx=5)

        profile_names = [p["name"] for p in self.profiles]
        self.profile_var = tk.StringVar(value=self.app_settings["current_profile"])
        self.profile_menu = ctk.CTkOptionMenu(
            selector_row, variable=self.profile_var, values=profile_names,
            command=self.on_profile_change, width=200
        )
        self.profile_menu.pack(side="left", padx=5)

        ctk.CTkButton(selector_row, text="➕ Add", command=self.add_profile,
                      width=60, fg_color="#2ecc71").pack(side="left", padx=5)
        ctk.CTkButton(selector_row, text="🗑️ Del", command=self.delete_profile,
                      width=60, fg_color="#e74c3c").pack(side="left", padx=5)

        date_row = ctk.CTkFrame(frame, fg_color="transparent")
        date_row.pack(fill="x", padx=10, pady=(0, 10))
        ctk.CTkLabel(date_row, text="View Date:",
                     font=("Arial", 14, "bold"), width=120, anchor="w").pack(side="left", padx=5)

        self.date_var = tk.StringVar(value=f"{self.current_date} (Today)")
        self.date_menu = ctk.CTkOptionMenu(
            date_row, variable=self.date_var, values=[],
            command=self.on_date_change, width=200
        )
        self.date_menu.pack(side="left", padx=5)
        self.refresh_date_menu()

        toggle_row = ctk.CTkFrame(frame, fg_color="transparent")
        toggle_row.pack(fill="x", padx=10, pady=(0, 10))
        self.generate_all_var = tk.BooleanVar(value=self.app_settings["generate_for_all"])
        ctk.CTkSwitch(
            toggle_row, text="Generate for ALL completed profiles",
            variable=self.generate_all_var, command=self.on_generate_toggle_change
        ).pack(side="left", padx=5)

        # Retry rounds - FIXED: Use StringVar instead of IntVar
        ctk.CTkLabel(toggle_row, text="Max Retries:", width=90, anchor="w").pack(side="left", padx=(20, 5))
        self.retry_var = tk.StringVar(value=str(self.app_settings.get("max_retry_rounds", MAX_RETRY_ROUNDS_DEFAULT)))
        retry_combo = ctk.CTkComboBox(
            toggle_row, variable=self.retry_var,
            values=[str(i) for i in range(11)],
            command=self.on_retry_change, width=80
        )
        retry_combo.pack(side="left", padx=5)

        # Duplicate check across dates
        self.check_dup_var = tk.BooleanVar(value=self.app_settings.get("check_duplicates_across_dates", False))
        ctk.CTkSwitch(
            toggle_row, text="Check duplicates across all dates",
            variable=self.check_dup_var, command=self.on_dup_check_change
        ).pack(side="left", padx=(20, 5))

        self.profile_status_label = ctk.CTkLabel(frame, text="", font=("Arial", 12, "bold"))
        self.profile_status_label.pack(anchor="w", padx=15, pady=(0, 10))
        self.update_profile_status_label()

    def build_config_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=5)
        ctk.CTkLabel(frame, text="Profile Configuration",
                     font=("Arial", 14, "bold")).pack(anchor="w", padx=10, pady=(10, 5))

        prompt_row = ctk.CTkFrame(frame, fg_color="transparent")
        prompt_row.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(prompt_row, text="Prompt File:", width=100, anchor="w").pack(side="left", padx=5)
        self.prompt_file_label = ctk.CTkLabel(prompt_row, text="", anchor="w",
                                              width=350, text_color="#aaaaaa")
        self.prompt_file_label.pack(side="left", padx=5)
        ctk.CTkButton(prompt_row, text="📂 Select", command=self.select_prompt_file,
                      fg_color="#9b59b6", width=80).pack(side="right", padx=5)

        name_row = ctk.CTkFrame(frame, fg_color="transparent")
        name_row.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(name_row, text="Macro Name:", width=100, anchor="w").pack(side="left", padx=5)
        self.macro_entry = ctk.CTkEntry(name_row, placeholder_text="e.g., FormatResume", width=300)
        self.macro_entry.pack(side="left", padx=5)
        self.macro_entry.bind("<FocusOut>", lambda e: self.save_profile_field("macro_name", self.macro_entry.get().strip()))

        file_row = ctk.CTkFrame(frame, fg_color="transparent")
        file_row.pack(fill="x", padx=10, pady=(5, 10))
        ctk.CTkLabel(file_row, text="DOCM File:", width=100, anchor="w").pack(side="left", padx=5)
        self.docm_file_label = ctk.CTkLabel(file_row, text="No file selected", anchor="w",
                                            width=350, text_color="#aaaaaa")
        self.docm_file_label.pack(side="left", padx=5)
        ctk.CTkButton(file_row, text="📂 Select", command=self.select_docm_file,
                      fg_color="#9b59b6", width=80).pack(side="right", padx=5)

    def build_input_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=10)
        ctk.CTkLabel(frame, text="Job Links (URLs separated by new lines or commas):",
                     font=("Arial", 12)).pack(anchor="w", padx=10, pady=(10, 0))

        input_row = ctk.CTkFrame(frame, fg_color="transparent")
        input_row.pack(fill="x", padx=10, pady=10)

        self.url_entry = ctk.CTkTextbox(input_row, width=500, height=60,
                                        fg_color="#2b2b2b", text_color="#ffffff")
        self.url_entry.pack(side="left", padx=(0, 10))
        self.url_entry.bind("<Return>", lambda e: self.add_job())

        btn_col = ctk.CTkFrame(input_row, fg_color="transparent")
        btn_col.pack(side="left")
        ctk.CTkButton(btn_col, text="➕ Add Links", command=self.add_job,
                      fg_color="#2ecc71", width=120).pack(pady=2)
        ctk.CTkButton(btn_col, text="📥 Import CSV", command=self.import_jobs,
                      fg_color="#3498db", width=120).pack(pady=2)

    def build_search_batch_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=5)

        row = ctk.CTkFrame(frame, fg_color="transparent")
        row.pack(fill="x", padx=10, pady=10)

        ctk.CTkLabel(row, text="🔍 Search:", font=("Arial", 12)).pack(side="left", padx=5)
        self.search_entry = ctk.CTkEntry(row, placeholder_text="Search URL or filename...",
                                         width=250)
        self.search_entry.pack(side="left", padx=5)
        self.search_entry.bind("<KeyRelease>", lambda e: self.on_search())

        ctk.CTkButton(row, text="Clear", command=self.clear_search,
                      width=60).pack(side="left", padx=5)

        ctk.CTkLabel(row, text="   Batch:", font=("Arial", 12)).pack(side="left", padx=(20, 5))
        ctk.CTkButton(row, text="Select All (Ctrl+A)", command=self.select_all_jobs,
                      width=120, fg_color="#34495e").pack(side="left", padx=2)
        ctk.CTkButton(row, text="Deselect (Ctrl+D)", command=self.deselect_all_jobs,
                      width=120, fg_color="#34495e").pack(side="left", padx=2)
        ctk.CTkButton(row, text="🗑️ Delete (Del)", command=self.delete_selected_jobs,
                      width=120, fg_color="#e74c3c").pack(side="left", padx=2)
        ctk.CTkButton(row, text="↻ Retry (Ctrl+R)", command=self.retry_selected_jobs,
                      width=120, fg_color="#f39c12").pack(side="left", padx=2)

    def build_job_list_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=10)

        queue_header = ctk.CTkFrame(frame, fg_color="transparent")
        queue_header.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(queue_header, text="Job Queue",
                     font=("Arial", 16, "bold")).pack(side="left", padx=5)
        ctk.CTkButton(queue_header, text="🗑️ Delete All", command=self.delete_all_jobs,
                      fg_color="#c0392b", width=100).pack(side="right", padx=5)

        self.scroll_frame = ctk.CTkScrollableFrame(frame, corner_radius=5, height=300)
        self.scroll_frame.pack(fill="x", padx=10, pady=(0, 10))

    def build_control_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=(0, 10))

        self.start_btn = ctk.CTkButton(frame, text="▶ Start (Ctrl+S)",
                                       command=self.start_automation, fg_color="#3498db")
        self.start_btn.pack(side="left", padx=10, pady=10)
        self.pause_btn = ctk.CTkButton(frame, text="⏸ Pause", command=self.toggle_pause,
                                       state="disabled", fg_color="#f39c12")
        self.pause_btn.pack(side="left", padx=10, pady=10)
        self.stop_btn = ctk.CTkButton(frame, text="⏹ Stop (Esc)", command=self.stop_automation,
                                      state="disabled", fg_color="#e74c3c")
        self.stop_btn.pack(side="left", padx=10, pady=10)

        ctk.CTkButton(frame, text="💾 Backup DB", command=self.manual_backup,
                      fg_color="#9b59b6").pack(side="right", padx=10, pady=10)

    def build_export_import_section(self, parent):
        frame = ctk.CTkFrame(parent, corner_radius=10)
        frame.pack(fill="x", padx=20, pady=(0, 20))
        ctk.CTkButton(frame, text="📤 Export Current View to CSV", command=self.export_jobs,
                      fg_color="#16a085").pack(side="left", padx=10, pady=10)

    # ========================================================================
    # PROFILE & DATE MANAGEMENT
    # ========================================================================
    def on_profile_change(self, selected_name: str):
        if self.is_running:
            messagebox.showwarning("Warning", "Stop automation before switching profiles.")
            self.profile_var.set(self.app_settings["current_profile"])
            return
        self.app_settings["current_profile"] = selected_name
        save_app_settings(self.app_settings)
        self.current_date = date.today().strftime("%Y-%m-%d")
        self.refresh_date_menu()
        self.refresh_profile_ui()
        self.reload_jobs_for_current_view()

    def on_date_change(self, selected_value: str):
        if self.is_running:
            messagebox.showwarning("Warning", "Stop automation before switching dates.")
            return
        if "(Today)" in selected_value:
            new_date = date.today().strftime("%Y-%m-%d")
        else:
            new_date = selected_value
        if new_date != self.current_date:
            self.current_date = new_date
            self.reload_jobs_for_current_view()

    def on_generate_toggle_change(self):
        self.app_settings["generate_for_all"] = self.generate_all_var.get()
        save_app_settings(self.app_settings)

    def on_retry_change(self, value):
        try:
            self.app_settings["max_retry_rounds"] = int(value)
            save_app_settings(self.app_settings)
        except ValueError:
            pass

    def on_dup_check_change(self):
        self.app_settings["check_duplicates_across_dates"] = self.check_dup_var.get()
        save_app_settings(self.app_settings)

    def refresh_date_menu(self):
        profile_name = self.profile_var.get()
        available_dates = self.db.get_available_dates(profile_name)
        today_str = date.today().strftime("%Y-%m-%d")
        display_values = [f"{today_str} (Today)"]
        for d in available_dates:
            if d != today_str:
                display_values.append(d)
        self.date_menu.configure(values=display_values)
        if self.current_date == today_str:
            self.date_var.set(f"{today_str} (Today)")
        else:
            self.date_var.set(self.current_date)

    def refresh_profile_ui(self):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        self.prompt_file_label.configure(
            text=os.path.basename(profile.get("prompt_path", "")) or "Not set"
        )
        self.docm_file_label.configure(
            text=os.path.basename(profile.get("docm_path", "")) or "No file selected"
        )
        self.macro_entry.delete(0, tk.END)
        self.macro_entry.insert(0, profile.get("macro_name", ""))
        self.update_profile_status_label()

    def update_profile_status_label(self):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        if is_profile_complete(profile):
            self.profile_status_label.configure(
                text=f"✅ Profile '{profile['name']}' is Fully Configured",
                text_color="#2ecc71"
            )
        else:
            missing = []
            if not profile.get("prompt_path"): missing.append("Prompt")
            if not profile.get("docm_path"): missing.append("DOCM File")
            if not profile.get("macro_name"): missing.append("Macro Name")
            self.profile_status_label.configure(
                text=f"⚠️ Profile '{profile['name']}' is Incomplete (Missing: {', '.join(missing)})",
                text_color="#f39c12"
            )

    def add_profile(self):
        name = ctk.CTkInputDialog(text="Enter new profile name:", title="Add Profile").get_input()
        if name and name.strip():
            name = name.strip()
            if any(p["name"] == name for p in self.profiles):
                messagebox.showerror("Error", "Profile name already exists!")
                return
            new_profile = {"name": name, "prompt_path": "", "docm_path": "", "macro_name": ""}
            self.profiles.append(new_profile)
            save_profiles(self.profiles)
            self.profile_menu.configure(values=[p["name"] for p in self.profiles])
            self.profile_var.set(name)
            self.on_profile_change(name)

    def delete_profile(self):
        current_name = self.profile_var.get()
        if len(self.profiles) <= 1:
            messagebox.showwarning("Warning", "Cannot delete the last profile.")
            return
        if messagebox.askyesno("Confirm", f"Delete profile '{current_name}'?"):
            self.profiles = [p for p in self.profiles if p["name"] != current_name]
            save_profiles(self.profiles)
            self.profile_menu.configure(values=[p["name"] for p in self.profiles])
            self.profile_var.set(self.profiles[0]["name"])
            self.on_profile_change(self.profiles[0]["name"])

    def save_profile_field(self, field: str, value: str):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        profile[field] = value
        save_profiles(self.profiles)
        self.update_profile_status_label()

    def select_prompt_file(self):
        file_path = filedialog.askopenfilename(
            title="Select Prompt Text File",
            filetypes=[("Text Files", "*.txt"), ("All Files", "*.*")]
        )
        if file_path:
            if not validate_prompt_file(file_path):
                if not messagebox.askyesno("Warning", 
                    "Prompt file appears empty or invalid. Continue anyway?"):
                    return
            self.save_profile_field("prompt_path", file_path)
            self.refresh_profile_ui()

    def select_docm_file(self):
        file_path = filedialog.askopenfilename(
            title="Select Word Macro File",
            filetypes=[("Word Macro-Enabled Document", "*.docm"), ("All Files", "*.*")]
        )
        if file_path:
            self.save_profile_field("docm_path", file_path)
            self.refresh_profile_ui()

    # ========================================================================
    # JOB MANAGEMENT
    # ========================================================================
    def load_saved_jobs(self):
        profile_name = self.profile_var.get()
        jobs = self.db.get_jobs(profile_name, self.current_date)
        for job in jobs:
            if job.status == "Running":
                job.status = "Pending"
                self.db.update_status(job.job_id, "Pending")
            self.jobs[job.job_id] = {
                "url": job.url,
                "status": job.status,
                "status_var": tk.StringVar(value=job.status),
                "filename1": job.filename1,
                "filename2": job.filename2,
                "profile_name": job.profile_name,
                "date": job.date,
                "selected_var": tk.BooleanVar(value=False)
            }
        self.render_all_jobs()
        self.update_statistics()

    def reload_jobs_for_current_view(self):
        self.jobs.clear()
        self.selected_job_ids.clear()
        for widget in self.scroll_frame.winfo_children():
            widget.destroy()
        self.load_saved_jobs()

    def add_job(self):
        raw_text = self.url_entry.get("0.0", tk.END).strip()
        if not raw_text:
            return

        raw_urls = [u.strip() for u in re.split(r'[\n,]+', raw_text) if u.strip()]
        valid_urls = []
        invalid_urls = []
        for url in raw_urls:
            if URL_REGEX.match(url):
                valid_urls.append(url)
            else:
                invalid_urls.append(url)

        if invalid_urls:
            logger.warning(f"Skipped {len(invalid_urls)} invalid URLs")

        if not valid_urls:
            messagebox.showwarning("Warning", "No valid URLs found.")
            return

        if self.generate_all_var.get():
            target_profiles = [p["name"] for p in self.profiles if is_profile_complete(p)]
        else:
            target_profiles = [self.profile_var.get()]

        total_added = 0
        total_duplicates = 0
        cross_date_warnings = []
        check_across_dates = self.app_settings.get("check_duplicates_across_dates", False)

        for url in valid_urls:
            # Cross-date duplicate check
            if check_across_dates:
                existing = self.db.url_exists_anywhere(url)
                if existing:
                    for loc in existing:
                        cross_date_warnings.append(
                            f"{url[:40]}... already in {loc['profile']} on {loc['date']}"
                        )

            for profile_name in target_profiles:
                job = Job(
                    job_id=str(uuid.uuid4())[:8],
                    url=url,
                    status="Pending",
                    profile_name=profile_name,
                    date=self.current_date
                )
                if self.db.add_job(job):
                    total_added += 1
                else:
                    total_duplicates += 1

        if cross_date_warnings:
            warning_text = "URLs found on other dates:\n\n" + "\n".join(cross_date_warnings[:10])
            if len(cross_date_warnings) > 10:
                warning_text += f"\n... and {len(cross_date_warnings) - 10} more"
            messagebox.showinfo("Duplicate Warning", warning_text)

        if total_duplicates > 0:
            logger.info(f"Skipped {total_duplicates} duplicate URLs")

        self.url_entry.delete("0.0", tk.END)
        self.reload_jobs_for_current_view()
        self.refresh_date_menu()

        if total_added > 0:
            logger.info(f"Added {total_added} job(s)")

    def render_all_jobs(self):
        for widget in self.scroll_frame.winfo_children():
            widget.destroy()

        jobs_to_render = self.jobs
        if self.search_query:
            jobs_to_render = {
                jid: jdata for jid, jdata in self.jobs.items()
                if (self.search_query.lower() in jdata["url"].lower() or
                    self.search_query.lower() in jdata.get("filename1", "").lower() or
                    self.search_query.lower() in jdata.get("filename2", "").lower())
            }

        for job_id in jobs_to_render:
            self.render_job_row(job_id)

    def render_job_row(self, job_id: str):
        job = self.jobs[job_id]
        row = ctk.CTkFrame(self.scroll_frame, corner_radius=5, fg_color="#2b2b2b")
        row.pack(fill="x", pady=2, padx=2)
        job["row_frame"] = row

        checkbox = ctk.CTkCheckBox(row, text="", variable=job["selected_var"],
                                   command=lambda jid=job_id: self.on_job_select(jid), width=20)
        checkbox.pack(side="left", padx=5)

        display_url = (job["url"][:URL_DISPLAY_LENGTH] + "...") if len(job["url"]) > URL_DISPLAY_LENGTH else job["url"]
        ctk.CTkLabel(row, text=display_url, anchor="w", width=220).pack(side="left", padx=5)

        filename1 = job.get("filename1", "")
        filename2 = job.get("filename2", "")
        display_text = f"{filename1}, {filename2}" if filename2 else filename1
        if len(display_text) > FILENAME_DISPLAY_LENGTH:
            display_text = display_text[:FILENAME_DISPLAY_LENGTH] + "..."

        job["filename_label"] = ctk.CTkLabel(
            row, text=display_text, anchor="w", width=180,
            text_color="#f1c40f", font=("Arial", 12, "bold")
        )
        job["filename_label"].pack(side="left", padx=5)

        status_menu = ctk.CTkOptionMenu(
            row, variable=job["status_var"], values=list(self.status_colors.keys()),
            command=lambda val, jid=job_id: self.on_manual_status_change(jid, val),
            width=130, fg_color=self.status_colors.get(job["status"], "gray")
        )
        status_menu.pack(side="left", padx=5)
        job["status_menu"] = status_menu

        ctk.CTkButton(row, text="🗑️", width=30,
                      command=lambda jid=job_id: self.delete_single_job(jid),
                      fg_color="#e74c3c").pack(side="right", padx=2)
        ctk.CTkButton(row, text="🌐", width=30,
                      command=lambda jid=job_id: self.open_url(jid),
                      fg_color="#34495e").pack(side="right", padx=2)
        ctk.CTkButton(row, text="📋", width=30,
                      command=lambda jid=job_id: self.copy_url(jid),
                      fg_color="#34495e").pack(side="right", padx=2)

        if job["status"] == "Failed":
            ctk.CTkButton(row, text="↻", width=30,
                          command=lambda jid=job_id: self.retry_single_job(jid),
                          fg_color="#f39c12").pack(side="right", padx=2)

        ctk.CTkButton(row, text="✏️", width=30,
                      command=lambda jid=job_id: self.edit_job(jid),
                      fg_color="#9b59b6").pack(side="right", padx=2)

        ctk.CTkButton(row, text="Send", width=60,
                      command=lambda jid=job_id: self.send_single_job(jid),
                      fg_color="#34495e").pack(side="right", padx=2)

    def on_job_select(self, job_id: str):
        if self.jobs[job_id]["selected_var"].get():
            self.selected_job_ids.add(job_id)
        else:
            self.selected_job_ids.discard(job_id)

    def select_all_jobs(self):
        for jid, jdata in self.jobs.items():
            jdata["selected_var"].set(True)
            self.selected_job_ids.add(jid)

    def deselect_all_jobs(self):
        for jid, jdata in self.jobs.items():
            jdata["selected_var"].set(False)
        self.selected_job_ids.clear()

    def delete_selected_jobs(self):
        if not self.selected_job_ids:
            return
        if messagebox.askyesno("Confirm", f"Delete {len(self.selected_job_ids)} selected jobs?"):
            self.db.delete_jobs(list(self.selected_job_ids))
            for jid in list(self.selected_job_ids):
                if "row_frame" in self.jobs[jid]:
                    self.jobs[jid]["row_frame"].destroy()
                del self.jobs[jid]
            self.selected_job_ids.clear()
            self.update_statistics()

    def retry_selected_jobs(self):
        if not self.selected_job_ids:
            return
        for jid in list(self.selected_job_ids):
            self.retry_single_job(jid)

    def copy_url(self, job_id: str):
        if job_id in self.jobs:
            self.clipboard_clear()
            self.clipboard_append(self.jobs[job_id]["url"])

    def open_url(self, job_id: str):
        if job_id in self.jobs:
            job = self.jobs[job_id]
            webbrowser.open(job["url"])
            if job.get("filename1"):
                self.clipboard_clear()
                self.clipboard_append(job["filename1"])

    def edit_job(self, job_id: str):
        if job_id not in self.jobs:
            return
        job = self.jobs[job_id]

        dialog = ctk.CTkToplevel(self)
        dialog.title("Edit Job")
        dialog.geometry("500x300")
        dialog.transient(self)
        dialog.grab_set()

        ctk.CTkLabel(dialog, text="URL:").pack(anchor="w", padx=20, pady=(20, 5))
        url_entry = ctk.CTkEntry(dialog, width=450)
        url_entry.pack(padx=20)
        url_entry.insert(0, job["url"])

        ctk.CTkLabel(dialog, text="Filename 1:").pack(anchor="w", padx=20, pady=(10, 5))
        fn1_entry = ctk.CTkEntry(dialog, width=450)
        fn1_entry.pack(padx=20)
        fn1_entry.insert(0, job.get("filename1", ""))

        ctk.CTkLabel(dialog, text="Filename 2:").pack(anchor="w", padx=20, pady=(10, 5))
        fn2_entry = ctk.CTkEntry(dialog, width=450)
        fn2_entry.pack(padx=20)
        fn2_entry.insert(0, job.get("filename2", ""))

        def save():
            new_url = url_entry.get().strip()
            if not URL_REGEX.match(new_url):
                messagebox.showerror("Error", "Invalid URL")
                return
            job["url"] = new_url
            job["filename1"] = fn1_entry.get().strip()
            job["filename2"] = fn2_entry.get().strip()
            self.db.update_job(Job(
                job_id=job_id,
                url=job["url"],
                status=job["status"],
                profile_name=job["profile_name"],
                date=job["date"],
                filename1=job["filename1"],
                filename2=job["filename2"]
            ))
            if "row_frame" in job:
                job["row_frame"].destroy()
            self.render_job_row(job_id)
            dialog.destroy()

        ctk.CTkButton(dialog, text="Save", command=save,
                      fg_color="#2ecc71").pack(pady=20)

    def retry_single_job(self, job_id: str):
        if job_id in self.jobs:
            self.jobs[job_id]["status"] = "Pending"
            self.jobs[job_id]["status_var"].set("Pending")
            if "status_menu" in self.jobs[job_id]:
                self.jobs[job_id]["status_menu"].configure(fg_color="gray")
            self.db.update_status(job_id, "Pending")
            if "row_frame" in self.jobs[job_id]:
                self.jobs[job_id]["row_frame"].destroy()
            self.render_job_row(job_id)
            self.update_statistics()

    def on_manual_status_change(self, job_id: str, new_status: str):
        """Confirm critical status changes."""
        if new_status in ["Done", "Failed"]:
            if not messagebox.askyesno("Confirm", 
                f"Change status to '{new_status}'? This may affect automation."):
                # Revert the dropdown
                self.jobs[job_id]["status_var"].set(self.jobs[job_id]["status"])
                return
        self.update_job_status(job_id, new_status)

    def update_job_status(self, job_id: str, new_status: str):
        if job_id in self.jobs:
            self.jobs[job_id]["status"] = new_status
            self.jobs[job_id]["status_var"].set(new_status)
            if "status_menu" in self.jobs[job_id]:
                self.jobs[job_id]["status_menu"].configure(
                    fg_color=self.status_colors.get(new_status, "gray")
                )
            self.db.update_status(job_id, new_status)
            self.update_statistics()

    def delete_single_job(self, job_id: str):
        if job_id in self.jobs:
            self.db.delete_job(job_id)
            if "row_frame" in self.jobs[job_id]:
                self.jobs[job_id]["row_frame"].destroy()
            del self.jobs[job_id]
            self.selected_job_ids.discard(job_id)
            self.update_statistics()

    def delete_all_jobs(self):
        if not self.jobs:
            return
        profile_name = self.profile_var.get()
        if messagebox.askyesno("Confirm", "Delete ALL jobs for this profile on this date?"):
            self.db.delete_all_for_profile_date(profile_name, self.current_date)
            for job_data in list(self.jobs.values()):
                if "row_frame" in job_data:
                    job_data["row_frame"].destroy()
            self.jobs.clear()
            self.selected_job_ids.clear()
            self.update_statistics()
            self.refresh_date_menu()

    def on_search(self):
        self.search_query = self.search_entry.get().strip()
        self.render_all_jobs()

    def clear_search(self):
        self.search_entry.delete(0, tk.END)
        self.search_query = ""
        self.render_all_jobs()

    def update_statistics(self):
        profile_name = self.profile_var.get()
        stats = self.db.get_statistics(profile_name, self.current_date)
        for status, lbl in self.stat_labels.items():
            if status == "total":
                lbl.configure(text=f"Total: {stats.get('total', 0)}")
            else:
                lbl.configure(text=f"{status}: {stats.get(status, 0)}")

    def export_jobs(self):
        profile_name = self.profile_var.get()
        filepath = filedialog.asksaveasfilename(
            title="Export Jobs",
            defaultextension=".csv",
            filetypes=[("CSV Files", "*.csv")],
            initialfile=f"jobs_{profile_name}_{self.current_date}.csv"
        )
        if filepath:
            if self.db.export_csv(profile_name, self.current_date, filepath):
                messagebox.showinfo("Success", f"Exported to {filepath}")
            else:
                messagebox.showerror("Error", "Export failed")

    def export_statistics(self):
        """Export statistics for all dates."""
        profile_name = self.profile_var.get()
        filepath = filedialog.asksaveasfilename(
            title="Export Statistics",
            defaultextension=".csv",
            filetypes=[("CSV Files", "*.csv")],
            initialfile=f"stats_{profile_name}.csv"
        )
        if filepath:
            if self.db.export_statistics_csv(profile_name, filepath):
                messagebox.showinfo("Success", f"Statistics exported to {filepath}")
            else:
                messagebox.showerror("Error", "Export failed")

    def import_jobs(self):
        filepath = filedialog.askopenfilename(
            title="Import Jobs",
            filetypes=[("CSV Files", "*.csv")]
        )
        if filepath:
            profile_name = self.profile_var.get()
            result = self.db.import_csv(filepath, profile_name, self.current_date)
            messagebox.showinfo(
                "Import",
                f"Imported: {result['imported']}\n"
                f"Skipped (duplicates): {result['skipped']}\n"
                f"Invalid: {result['invalid']}"
            )
            self.reload_jobs_for_current_view()
            self.refresh_date_menu()

    def manual_backup(self):
        path = self.db.backup()
        if path:
            messagebox.showinfo("Backup", f"Backup saved to:\n{path}")
        else:
            messagebox.showerror("Error", "Backup failed")

    # ========================================================================
    # AUTOMATION CONTROL
    # ========================================================================
    def start_automation(self):
        if self.is_running:
            return
        if not self.ws_manager.has_clients():
            messagebox.showwarning("Warning", "Browser extension is not connected.")
            return

        self.is_running = True
        self.is_paused = False
        self.retry_round = 0
        self.start_btn.configure(state="disabled")
        self.pause_btn.configure(state="normal", text="⏸ Pause")
        self.stop_btn.configure(state="normal")

        self.profile_menu.configure(state="disabled")
        self.date_menu.configure(state="disabled")

        for job_id, job in self.jobs.items():
            if job["status"] == "Pending":
                if self.generate_all_var.get():
                    job["profiles_to_process"] = [p["name"] for p in self.profiles if is_profile_complete(p)]
                else:
                    current_p = next((p for p in self.profiles if p["name"] == self.app_settings["current_profile"]), None)
                    job["profiles_to_process"] = [current_p["name"]] if current_p and is_profile_complete(current_p) else []

        self.update_progress()
        self._process_jobs_iteratively()

    def _process_jobs_iteratively(self):
        """FIXED: Iterative job processing instead of recursive."""
        if not self.is_running or self.is_paused:
            return
        
        max_rounds = self.app_settings.get("max_retry_rounds", MAX_RETRY_ROUNDS_DEFAULT)
        
        while self.is_running and not self.is_paused:
            job_id = self.get_next_pending_job()
            if job_id:
                self.process_profile_for_job(job_id)
                return  # Wait for response
            
            # No pending jobs - check if we should retry
            if self.retry_round < max_rounds:
                failed_jobs = [jid for jid, data in self.jobs.items() if data["status"] == "Failed"]
                if failed_jobs:
                    logger.info(f"🔄 Round {self.retry_round + 1} complete. Retrying {len(failed_jobs)} failed jobs...")
                    for jid in failed_jobs:
                        if self.generate_all_var.get():
                            self.jobs[jid]["profiles_to_process"] = [p["name"] for p in self.profiles if is_profile_complete(p)]
                        else:
                            current_p = next((p for p in self.profiles if p["name"] == self.app_settings["current_profile"]), None)
                            self.jobs[jid]["profiles_to_process"] = [current_p["name"]] if current_p and is_profile_complete(current_p) else []
                        self.jobs[jid]["status"] = "Pending"
                        self.jobs[jid]["status_var"].set("Pending")
                        if "status_menu" in self.jobs[jid]:
                            self.jobs[jid]["status_menu"].configure(fg_color="gray")
                        self.db.update_status(jid, "Pending")
                        if "row_frame" in self.jobs[jid]:
                            self.jobs[jid]["row_frame"].destroy()
                        self.render_job_row(jid)
                    self.retry_round += 1
                    self.update_progress()
                    continue  # Loop again to process the retried jobs
                else:
                    logger.info("✅ All jobs processed successfully.")
                    break
            else:
                logger.info("✅ Max retry rounds reached. Ending automation.")
                break
        
        self.stop_automation()

    def toggle_pause(self):
        self.is_paused = not self.is_paused
        self.pause_btn.configure(
            text="▶ Resume" if self.is_paused else "⏸ Pause",
            fg_color="#2ecc71" if self.is_paused else "#f39c12"
        )
        if not self.is_paused:
            self._process_jobs_iteratively()

    def stop_automation(self):
        self.is_running = False
        self.is_paused = False
        self.retry_round = 0
        self.start_btn.configure(state="normal")
        self.pause_btn.configure(state="disabled", text="⏸ Pause")
        self.stop_btn.configure(state="disabled")
        self.progress_label.configure(text="")

        self.profile_menu.configure(state="normal")
        self.date_menu.configure(state="normal")

    def update_progress(self):
        if not self.is_running:
            return
        pending = sum(1 for j in self.jobs.values() if j["status"] == "Pending")
        running = sum(1 for j in self.jobs.values() if j["status"] == "Running")
        total = len(self.jobs)
        done = total - pending - running
        self.progress_label.configure(
            text=f"⏳ Progress: {done}/{total} complete | {running} running | {pending} pending | Round {self.retry_round + 1}"
        )

    def get_next_pending_job(self) -> Optional[str]:
        for job_id, data in self.jobs.items():
            if data["status"] == "Pending":
                return job_id
        return None

    def process_profile_for_job(self, job_id: str):
        job = self.jobs[job_id]
        if not job.get("profiles_to_process"):
            if job["status"] == "Resume Received":
                self.update_job_status(job_id, "Done")
            else:
                self.update_job_status(job_id, "Failed")
            self._process_jobs_iteratively()
            return

        profile_name = job["profiles_to_process"].pop(0)
        job["current_profile"] = profile_name
        self.ws_manager.set_job_profile(job_id, profile_name)

        self.update_job_status(job_id, "Running")
        self.update_progress()
        payload = {
            "type": "START_TASK",
            "jobId": job_id,
            "jobUrl": job["url"],
            "profileName": profile_name
        }
        self.ws_manager.send_to_clients(json.dumps(payload))

    def send_single_job(self, job_id: str):
        job = self.jobs[job_id]
        if job["status"] == "Pending":
            if self.generate_all_var.get():
                job["profiles_to_process"] = [p["name"] for p in self.profiles if is_profile_complete(p)]
            else:
                current_p = next((p for p in self.profiles if p["name"] == self.app_settings["current_profile"]), None)
                job["profiles_to_process"] = [current_p["name"]] if current_p and is_profile_complete(current_p) else []

            if not job["profiles_to_process"]:
                self.update_job_status(job_id, "Failed")
                return

            self.ws_manager.set_job_profile(job_id, job["profiles_to_process"][0])
            self.update_job_status(job_id, "Running")
            payload = {
                "type": "START_TASK",
                "jobId": job_id,
                "jobUrl": job["url"],
                "profileName": job["profiles_to_process"].pop(0)
            }
            self.ws_manager.send_to_clients(json.dumps(payload))

    # ========================================================================
    # QUEUE POLLING & TIMEOUT CHECK
    # ========================================================================
    def poll_queues(self):
        try:
            while True:
                message = self.ws_manager.ws_to_ui_queue.get_nowait()
                data = json.loads(message)
                msg_type = data.get("type")
                job_id = data.get("jobId")

                if msg_type == "TASK_COMPLETE":
                    resume_text = data.get("resumeText", "")
                    marker = "[FolderName]:"

                    # Mark job as completed in tracking
                    self.ws_manager.complete_job(job_id)

                    if marker not in resume_text:
                        logger.error(f"❌ Job {job_id}: Marker '{marker}' not found in resume")
                        self.update_job_status(job_id, "Failed")
                        self._process_jobs_iteratively()
                        continue

                    idx = resume_text.index(marker) + len(marker)
                    remaining = resume_text[idx:].strip()
                    parts = [p for p in re.split(r'[,\s]+', remaining) if p]

                    filename1 = parts[0] if len(parts) > 0 else ""
                    filename2 = parts[1] if len(parts) > 1 else ""

                    if job_id in self.jobs:
                        self.jobs[job_id]["filename1"] = filename1
                        self.jobs[job_id]["filename2"] = filename2
                        self.db.update_filenames(job_id, filename1, filename2)

                        if "filename_label" in self.jobs[job_id]:
                            display_text = f"{filename1}, {filename2}" if filename2 else filename1
                            self.jobs[job_id]["filename_label"].configure(text=display_text)

                    self.update_job_status(job_id, "Resume Received")

                    profiles_to_run = []
                    current_profile_name = self.jobs[job_id].get("current_profile", self.app_settings["current_profile"])
                    current_p = next((p for p in self.profiles if p["name"] == current_profile_name), None)
                    if current_p and is_profile_complete(current_p):
                        profiles_to_run = [current_p]

                    if profiles_to_run and resume_text:
                        logger.info(f"🚀 Starting Word macro for profile '{current_profile_name}'")
                        threading.Thread(
                            target=self.run_macros_for_profiles,
                            args=(job_id, resume_text, profiles_to_run),
                            daemon=True
                        ).start()
                        self._process_jobs_iteratively()
                    else:
                        logger.warning("⚠️ Skipping macro - profile incomplete")
                        self.update_job_status(job_id, "Failed")
                        self._process_jobs_iteratively()

                elif msg_type == "TASK_FAILED":
                    self.ws_manager.complete_job(job_id)
                    self.update_job_status(job_id, "Failed")
                    self._process_jobs_iteratively()

                elif msg_type == "MACRO_SUCCESS":
                    if job_id in self.jobs and self.jobs[job_id].get("profiles_to_process"):
                        self.process_profile_for_job(job_id)
                    else:
                        self.update_job_status(job_id, "Done")
                    self.update_progress()

                elif msg_type == "MACRO_FAILED":
                    self.update_job_status(job_id, "Failed")
                    self.update_progress()

                elif msg_type == "EXTENSION_STATUS":
                    status = data.get("status")
                    connected = status == 'connected'
                    self.header.configure(
                        text=f"🚀 Job Automation Dashboard ({'🟢 Connected' if connected else '🔴 Disconnected'})"
                    )
                    self.start_btn.configure(state="normal" if connected else "disabled")

                self.ws_manager.ws_to_ui_queue.task_done()
        except queue.Empty:
            pass
        self.after(UI_POLL_INTERVAL_MS, self.poll_queues)

    def check_timeouts(self):
        """Periodically check for timed-out jobs."""
        if self.is_running:
            timed_out = self.ws_manager.get_timed_out_jobs()
            for job_id in timed_out:
                logger.warning(f"⏰ Job {job_id} timed out after {WS_TIMEOUT_SECONDS}s")
                self.ws_manager.complete_job(job_id)
                if job_id in self.jobs:
                    self.update_job_status(job_id, "Failed")
            if timed_out:
                self._process_jobs_iteratively()
        self.after(5000, self.check_timeouts)  # Check every 5 seconds

    def run_macros_for_profiles(self, job_id: str, resume_text: str, 
                                profiles: List[Dict]):
        all_success = True
        for profile in profiles:
            success = run_word_macro(
                resume_text, profile["docm_path"],
                profile["macro_name"], profile["name"]
            )
            if not success:
                all_success = False

        if all_success:
            self.ws_manager.ws_to_ui_queue.put(json.dumps({"type": "MACRO_SUCCESS", "jobId": job_id}))
        else:
            self.ws_manager.ws_to_ui_queue.put(json.dumps({"type": "MACRO_FAILED", "jobId": job_id}))


if __name__ == "__main__":
    app = JobAutomatorApp()
    app.mainloop()