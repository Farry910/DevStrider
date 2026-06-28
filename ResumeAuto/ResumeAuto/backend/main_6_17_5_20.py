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
from datetime import datetime, date

# --- Global State & Thread-Safe Queues ---
ui_to_ws_queue = queue.Queue()
ws_to_ui_queue = queue.Queue()
connected_clients = set()
SETTINGS_FILE = "app_settings.json"
PROFILES_FILE = "profiles.json"
JOBS_BASE_DIR = "jobs_data"

# Thread-safe map to track which profile is currently being processed for a specific job
job_profile_map = {}

# --- Profile & Settings Management ---
def load_profiles():
    if os.path.exists(PROFILES_FILE):
        try:
            with open(PROFILES_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception: pass
    return [{"name": "Default", "prompt_path": "prompt.txt", "docm_path": "", "macro_name": ""}]

def save_profiles(profiles):
    with open(PROFILES_FILE, "w", encoding="utf-8") as f:
        json.dump(profiles, f, indent=2)

def is_profile_complete(profile):
    return bool(profile.get("prompt_path") and profile.get("docm_path") and profile.get("macro_name"))

def load_app_settings():
    if os.path.exists(SETTINGS_FILE):
        try:
            with open(SETTINGS_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
                data.setdefault("current_profile", "Default")
                data.setdefault("generate_for_all", False)
                return data
        except Exception: pass
    return {"current_profile": "Default", "generate_for_all": False}

def save_app_settings(current_profile, generate_for_all):
    with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
        json.dump({"current_profile": current_profile, "generate_for_all": generate_for_all}, f, indent=2)

# --- Job File Management (Per Profile + Per Date) ---
def get_safe_name(name):
    """Generate a safe folder/file name"""
    return re.sub(r'[^\w\-]', '_', name)

def get_jobs_dir(profile_name):
    """Get the directory for a profile's jobs"""
    safe_name = get_safe_name(profile_name)
    return os.path.join(JOBS_BASE_DIR, safe_name)

def get_jobs_file(profile_name, date_str):
    """Get the jobs file for a specific profile and date"""
    return os.path.join(get_jobs_dir(profile_name), f"{date_str}.json")

def get_available_dates(profile_name):
    """Get list of dates that have job files for this profile, sorted newest first"""
    jobs_dir = get_jobs_dir(profile_name)
    if not os.path.exists(jobs_dir):
        return []
    dates = []
    for filename in os.listdir(jobs_dir):
        if filename.endswith(".json"):
            date_str = filename[:-5]  # Remove .json
            # Validate it's a proper date
            try:
                datetime.strptime(date_str, "%Y-%m-%d")
                dates.append(date_str)
            except ValueError:
                continue
    # Sort newest first
    dates.sort(reverse=True)
    return dates

def load_jobs_from_disk(profile_name, date_str):
    """Load jobs for a specific profile and date"""
    jobs_file = get_jobs_file(profile_name, date_str)
    if not os.path.exists(jobs_file): return []
    try:
        with open(jobs_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            for job in data:
                # Only reset "Running" jobs (interrupted), keep other states
                if job.get("status") == "Running":
                    job["status"] = "Pending"
                job.setdefault("filename1", "")
                job.setdefault("filename2", "")
            return data
    except Exception:
        return []

def save_jobs_to_disk(jobs_dict, profile_name, date_str):
    """Save all jobs for a specific profile and date"""
    jobs_file = get_jobs_file(profile_name, date_str)
    # Ensure directory exists
    os.makedirs(os.path.dirname(jobs_file), exist_ok=True)
    serializable = []
    for jid, data in jobs_dict.items():
        serializable.append({
            "job_id": jid, 
            "url": data["url"], 
            "status": data["status"],
            "filename1": data.get("filename1", ""),
            "filename2": data.get("filename2", "")
        })
    with open(jobs_file, "w", encoding="utf-8") as f:
        json.dump(serializable, f, indent=2)

# --- PowerShell Macro Execution ---
def run_word_macro(resume_text, docm_path, macro_name, profile_name):
    docm_path = os.path.normpath(docm_path)
    if not os.path.exists(docm_path):
        print(f"❌ [Macro:{profile_name}] Error: DOCM file not found at: {docm_path}")
        return False

    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.txt') as f:
        f.write(resume_text)
        temp_txt_path = f.name

    ps_script = r"""
param ([string]$TempTextPath, [string]$DocmPath, [string]$MacroName, [string]$ProfileName)
$word = $null
try {
    if (-not (Test-Path $TempTextPath)) { throw "Temp text file not found." }
    
    $bridgeFile = Join-Path $env:TEMP "resume_bridge_path.txt"
    [System.IO.File]::WriteAllText($bridgeFile, $TempTextPath, [System.Text.Encoding]::UTF8)
    
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

    Write-Host "Waiting for Word to close (Max 10 seconds)..."
    $startTime = Get-Date
    while ($true) {
        Start-Sleep -Milliseconds 500
        try {
            $null = $word.Name 
            $elapsed = ((Get-Date) - $startTime).TotalSeconds
            if ($elapsed -gt 10) {
                Write-Host "FAILED: Word process did not close within 10 seconds."
                $word.Quit([ref]0) 
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
                exit 1 
            }
        } catch { break }
    }
    Write-Output "SUCCESS"
    exit 0
} catch {
    Write-Host "POWERSHELL ERROR: $($_.Exception.Message)"
    if ($word) {
        try { $word.Quit([ref]0); [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null } catch {}
    }
    exit 1
} finally {
    if ($TempTextPath -and (Test-Path $TempTextPath)) { Remove-Item $TempTextPath -Force -ErrorAction SilentlyContinue }
    [System.GC]::Collect(); [System.GC]::WaitForPendingFinalizers()
}
"""
    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.ps1') as f:
        f.write(ps_script)
        ps_script_path = f.name

    try:
        print(f"⏳ [Macro:{profile_name}] Executing PowerShell script...")
        subprocess.run(['powershell.exe', '-ExecutionPolicy', 'Bypass', '-File', ps_script_path,
                        '-TempTextPath', temp_txt_path, '-DocmPath', docm_path, 
                        '-MacroName', macro_name, '-ProfileName', profile_name],
                       capture_output=True, text=True, check=True)
        print(f"✅ [Macro:{profile_name}] PowerShell script executed successfully.")
        return True
    except subprocess.CalledProcessError as e:
        print(f"❌ [Macro:{profile_name}] PowerShell script failed:")
        if e.stdout: print(f"STDOUT:\n{e.stdout}")
        if e.stderr: print(f"STDERR:\n{e.stderr}")
        return False
    except Exception as e:
        print(f"❌ [Macro:{profile_name}] Unexpected error: {e}")
        return False
    finally:
        if os.path.exists(ps_script_path): os.remove(ps_script_path)
        if os.path.exists(temp_txt_path):
            try: os.remove(temp_txt_path)
            except: pass

# --- WebSocket Server Logic ---
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

async def ws_handler(websocket):
    connected_clients.add(websocket)
    print("✅ Extension connected.")
    ws_to_ui_queue.put(json.dumps({"type": "EXTENSION_STATUS", "status": "connected"}))
    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                if data.get("type") == "PARSE_REQUEST":
                    raw_text = data.get("raw_text", "")
                    job_id = data.get("jobId", "")
                    print(f"🔄 Parsing job data for {job_id}...")
                    
                    profile_name = job_profile_map.get(job_id, "Default")
                    
                    profiles = load_profiles()
                    current_p = next((p for p in profiles if p["name"] == profile_name), profiles[0])
                    prompt_text = "Act as an expert resume writer."
                    if os.path.exists(current_p.get("prompt_path", "")):
                        with open(current_p["prompt_path"], "r", encoding="utf-8") as f:
                            prompt_text = f.read().strip()
                    
                    response = {"type": "PARSE_RESPONSE", "jobId": job_id, "prompt": prompt_text, "job_description": parse_job_description(raw_text)}
                    await websocket.send(json.dumps(response))
                elif data.get("type") in ["TASK_COMPLETE", "TASK_FAILED"]:
                    ws_to_ui_queue.put(message)
            except json.JSONDecodeError: pass
    except websockets.exceptions.ConnectionClosed: pass
    finally:
        connected_clients.remove(websocket)
        print("❌ Extension disconnected.")
        ws_to_ui_queue.put(json.dumps({"type": "EXTENSION_STATUS", "status": "disconnected"}))

async def ws_broadcaster():
    while True:
        try:
            message = ui_to_ws_queue.get_nowait()
            if connected_clients: websockets.broadcast(connected_clients, message)
            ui_to_ws_queue.task_done()
        except queue.Empty: await asyncio.sleep(0.1)

def start_asyncio_loop():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    async def run_server():
        async with websockets.serve(ws_handler, "localhost", 12345, ping_interval=None):
            print("🚀 WebSocket Server running on ws://localhost:12345")
            asyncio.create_task(ws_broadcaster())
            await asyncio.Future()
    try: loop.run_until_complete(run_server())
    except Exception as e: print(f"WebSocket server stopped: {e}")
    finally: loop.close()

# --- Modern UI Application ---
class JobAutomatorApp(ctk.CTk):
    def __init__(self):
        super().__init__()
        self.title("Job to ChatGPT Automator")
        self.geometry("950x850")
        ctk.set_appearance_mode("Dark")
        ctk.set_default_color_theme("blue")
        self.minsize(800, 600)
        
        self.main_scroll = ctk.CTkScrollableFrame(self, corner_radius=0)
        self.main_scroll.pack(fill="both", expand=True)
        
        self.is_running = False
        self.is_paused = False
        self.retry_round = 0
        self.jobs = {}
        self.profiles = load_profiles()
        self.app_settings = load_app_settings()
        
        # Current viewing date (defaults to today)
        self.current_date = date.today().strftime("%Y-%m-%d")
        
        self.status_colors = {"Pending": "gray", "Running": "#3498db", "Resume Received": "#f39c12", "Done": "#2ecc71", "Failed": "#e74c3c"}

        self.build_ui()
        self.load_saved_jobs()
        self.poll_queues()
        
        threading.Thread(target=start_asyncio_loop, daemon=True).start()

    def build_ui(self):
        parent = self.main_scroll

        self.header = ctk.CTkLabel(parent, text="🚀 Job Automation Dashboard", font=("Arial", 24, "bold"))
        self.header.pack(pady=(20, 10))

        profile_frame = ctk.CTkFrame(parent, corner_radius=10)
        profile_frame.pack(fill="x", padx=20, pady=5)
        
        selector_row = ctk.CTkFrame(profile_frame, fg_color="transparent")
        selector_row.pack(fill="x", padx=10, pady=10)
        ctk.CTkLabel(selector_row, text="Active Profile:", font=("Arial", 14, "bold"), width=120, anchor="w").pack(side="left", padx=5)
        
        profile_names = [p["name"] for p in self.profiles]
        self.profile_var = tk.StringVar(value=self.app_settings["current_profile"])
        self.profile_menu = ctk.CTkOptionMenu(selector_row, variable=self.profile_var, values=profile_names, 
                                              command=self.on_profile_change, width=200)
        self.profile_menu.pack(side="left", padx=5)
        
        ctk.CTkButton(selector_row, text="➕ Add", command=self.add_profile, width=60, fg_color="#2ecc71").pack(side="left", padx=5)
        ctk.CTkButton(selector_row, text="🗑️ Del", command=self.delete_profile, width=60, fg_color="#e74c3c").pack(side="left", padx=5)

        # Date selector row
        date_row = ctk.CTkFrame(profile_frame, fg_color="transparent")
        date_row.pack(fill="x", padx=10, pady=(0, 10))
        ctk.CTkLabel(date_row, text="View Date:", font=("Arial", 14, "bold"), width=120, anchor="w").pack(side="left", padx=5)
        
        self.date_var = tk.StringVar(value=f"{self.current_date} (Today)")
        self.date_menu = ctk.CTkOptionMenu(date_row, variable=self.date_var, values=[], 
                                           command=self.on_date_change, width=200)
        self.date_menu.pack(side="left", padx=5)
        self.refresh_date_menu()

        toggle_row = ctk.CTkFrame(profile_frame, fg_color="transparent")
        toggle_row.pack(fill="x", padx=10, pady=(0, 10))
        self.generate_all_var = tk.BooleanVar(value=self.app_settings["generate_for_all"])
        self.generate_all_switch = ctk.CTkSwitch(toggle_row, text="Generate resumes for ALL completed profiles (instead of just current)", 
                                                 variable=self.generate_all_var, command=self.on_generate_toggle_change)
        self.generate_all_switch.pack(side="left", padx=5)

        self.profile_status_label = ctk.CTkLabel(profile_frame, text="", font=("Arial", 12, "bold"))
        self.profile_status_label.pack(anchor="w", padx=15, pady=(0, 10))
        self.update_profile_status_label()

        config_frame = ctk.CTkFrame(parent, corner_radius=10)
        config_frame.pack(fill="x", padx=20, pady=5)
        ctk.CTkLabel(config_frame, text="Profile Configuration", font=("Arial", 14, "bold")).pack(anchor="w", padx=10, pady=(10, 5))
        
        prompt_row = ctk.CTkFrame(config_frame, fg_color="transparent")
        prompt_row.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(prompt_row, text="Prompt File:", width=100, anchor="w").pack(side="left", padx=5)
        self.prompt_file_label = ctk.CTkLabel(prompt_row, text="", anchor="w", width=350, text_color="#aaaaaa")
        self.prompt_file_label.pack(side="left", padx=5)
        ctk.CTkButton(prompt_row, text="📂 Select", command=self.select_prompt_file, fg_color="#9b59b6", width=80).pack(side="right", padx=5)

        name_row = ctk.CTkFrame(config_frame, fg_color="transparent")
        name_row.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(name_row, text="Macro Name:", width=100, anchor="w").pack(side="left", padx=5)
        self.macro_entry = ctk.CTkEntry(name_row, placeholder_text="e.g., FormatResume", width=300)
        self.macro_entry.pack(side="left", padx=5)
        self.macro_entry.bind("<FocusOut>", lambda e: self.save_profile_field("macro_name", self.macro_entry.get().strip()))

        file_row = ctk.CTkFrame(config_frame, fg_color="transparent")
        file_row.pack(fill="x", padx=10, pady=(5, 10))
        ctk.CTkLabel(file_row, text="DOCM File:", width=100, anchor="w").pack(side="left", padx=5)
        self.docm_file_label = ctk.CTkLabel(file_row, text="No file selected", anchor="w", width=350, text_color="#aaaaaa")
        self.docm_file_label.pack(side="left", padx=5)
        ctk.CTkButton(file_row, text="📂 Select", command=self.select_docm_file, fg_color="#9b59b6", width=80).pack(side="right", padx=5)

        input_frame = ctk.CTkFrame(parent, corner_radius=10)
        input_frame.pack(fill="x", padx=20, pady=10)
        ctk.CTkLabel(input_frame, text="Job Links (paste multiple links separated by new lines or commas):", font=("Arial", 12)).pack(anchor="w", padx=10, pady=(10, 0))
        self.url_entry = ctk.CTkTextbox(input_frame, width=500, height=60, fg_color="#2b2b2b", text_color="#ffffff")
        self.url_entry.pack(side="left", padx=10, pady=10)
        ctk.CTkButton(input_frame, text="➕ Add Links", command=self.add_job, fg_color="#2ecc71", width=100).pack(side="left", padx=10, pady=10)

        list_frame = ctk.CTkFrame(parent, corner_radius=10)
        list_frame.pack(fill="x", padx=20, pady=10)
        
        queue_header = ctk.CTkFrame(list_frame, fg_color="transparent")
        queue_header.pack(fill="x", padx=10, pady=5)
        ctk.CTkLabel(queue_header, text="Job Queue", font=("Arial", 16, "bold")).pack(side="left", padx=5)
        ctk.CTkButton(queue_header, text="🗑️ Delete All", command=self.delete_all_jobs, fg_color="#c0392b", width=100).pack(side="right", padx=5)
        
        self.scroll_frame = ctk.CTkScrollableFrame(list_frame, corner_radius=5, height=300)
        self.scroll_frame.pack(fill="x", padx=10, pady=(0, 10))

        control_frame = ctk.CTkFrame(parent, corner_radius=10)
        control_frame.pack(fill="x", padx=20, pady=(0, 20))
        self.start_btn = ctk.CTkButton(control_frame, text="▶ Start Automation", command=self.start_automation, fg_color="#3498db")
        self.start_btn.pack(side="left", padx=10, pady=10)
        self.pause_btn = ctk.CTkButton(control_frame, text="⏸ Pause", command=self.toggle_pause, state="disabled", fg_color="#f39c12")
        self.pause_btn.pack(side="left", padx=10, pady=10)
        self.stop_btn = ctk.CTkButton(control_frame, text="⏹ Stop", command=self.stop_automation, state="disabled", fg_color="#e74c3c")
        self.stop_btn.pack(side="left", padx=10, pady=10)

        self.refresh_profile_ui()

    def refresh_date_menu(self):
        """Refresh the date dropdown with available dates for current profile"""
        profile_name = self.profile_var.get()
        available_dates = get_available_dates(profile_name)
        
        # Build display values - always include today
        today_str = date.today().strftime("%Y-%m-%d")
        display_values = [f"{today_str} (Today)"]
        
        for d in available_dates:
            if d != today_str:
                display_values.append(d)
        
        self.date_menu.configure(values=display_values)
        
        # Set current selection
        if self.current_date == today_str:
            self.date_var.set(f"{today_str} (Today)")
        else:
            self.date_var.set(self.current_date)

    def on_date_change(self, selected_value):
        """Handle date selection change"""
        # Parse the date from the display value
        if "(Today)" in selected_value:
            new_date = date.today().strftime("%Y-%m-%d")
        else:
            new_date = selected_value
        
        if new_date != self.current_date:
            self.current_date = new_date
            self.reload_jobs_for_current_view()

    def on_profile_change(self, selected_name):
        self.app_settings["current_profile"] = selected_name
        save_app_settings(selected_name, self.generate_all_var.get())
        self.refresh_profile_ui()
        # Reset to today when switching profiles
        self.current_date = date.today().strftime("%Y-%m-%d")
        self.refresh_date_menu()
        self.reload_jobs_for_current_view()

    def reload_jobs_for_current_view(self):
        """Clear current job list and reload from the current profile+date"""
        # Clear existing jobs from UI
        for job_data in self.jobs.values():
            if "row_frame" in job_data:
                job_data["row_frame"].destroy()
        self.jobs.clear()
        # Load jobs for current profile+date
        self.load_saved_jobs()

    def on_generate_toggle_change(self):
        self.app_settings["generate_for_all"] = self.generate_all_var.get()
        save_app_settings(self.app_settings["current_profile"], self.app_settings["generate_for_all"])

    def refresh_profile_ui(self):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        self.prompt_file_label.configure(text=os.path.basename(profile.get("prompt_path", "")) or "Not set")
        self.docm_file_label.configure(text=os.path.basename(profile.get("docm_path", "")) or "No file selected")
        self.macro_entry.delete(0, tk.END)
        self.macro_entry.insert(0, profile.get("macro_name", ""))
        self.update_profile_status_label()

    def update_profile_status_label(self):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        if is_profile_complete(profile):
            self.profile_status_label.configure(text=f"✅ Profile '{profile['name']}' is Fully Configured", text_color="#2ecc71")
        else:
            missing = []
            if not profile.get("prompt_path"): missing.append("Prompt")
            if not profile.get("docm_path"): missing.append("DOCM File")
            if not profile.get("macro_name"): missing.append("Macro Name")
            self.profile_status_label.configure(text=f"⚠️ Profile '{profile['name']}' is Incomplete (Missing: {', '.join(missing)})", text_color="#f39c12")

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

    def save_profile_field(self, field, value):
        profile = next((p for p in self.profiles if p["name"] == self.profile_var.get()), self.profiles[0])
        profile[field] = value
        save_profiles(self.profiles)
        self.update_profile_status_label()

    def select_prompt_file(self):
        file_path = filedialog.askopenfilename(title="Select Prompt Text File", filetypes=[("Text Files", "*.txt"), ("All Files", "*.*")])
        if file_path:
            self.save_profile_field("prompt_path", file_path)
            self.refresh_profile_ui()

    def select_docm_file(self):
        file_path = filedialog.askopenfilename(title="Select Word Macro File", filetypes=[("Word Macro-Enabled Document", "*.docm"), ("All Files", "*.*")])
        if file_path:
            self.save_profile_field("docm_path", file_path)
            self.refresh_profile_ui()

    def load_saved_jobs(self):
        """Load jobs for current profile and current date"""
        profile_name = self.profile_var.get()
        saved_jobs = load_jobs_from_disk(profile_name, self.current_date)
        for job_data in saved_jobs:
            job_id = job_data["job_id"]
            self.jobs[job_id] = {
                "url": job_data["url"], 
                "status": job_data["status"], 
                "status_var": tk.StringVar(value=job_data["status"]),
                "filename1": job_data.get("filename1", ""),
                "filename2": job_data.get("filename2", ""),
                "profile_name": profile_name,
                "date": self.current_date
            }
            self.render_job_row(job_id)

    def add_job(self):
        raw_text = self.url_entry.get("0.0", tk.END).strip()
        if not raw_text: return
        urls = [u.strip() for u in re.split(r'[\n,]+', raw_text) if u.strip().startswith('http')]
        if not urls: return
        
        if self.generate_all_var.get():
            target_profiles = [p["name"] for p in self.profiles if is_profile_complete(p)]
        else:
            target_profiles = [self.profile_var.get()]
        
        for url in urls:
            for profile_name in target_profiles:
                job_id = str(uuid.uuid4())[:8]
                self.jobs[job_id] = {
                    "url": url, 
                    "status": "Pending", 
                    "status_var": tk.StringVar(value="Pending"),
                    "filename1": "",
                    "filename2": "",
                    "profile_name": profile_name,
                    "date": self.current_date
                }
                self.render_job_row(job_id)
        
        # Save all jobs for each affected profile on current date
        for profile_name in target_profiles:
            self.save_profile_jobs(profile_name)
        
        # Refresh date menu in case this is a new date
        self.refresh_date_menu()
        self.url_entry.delete("0.0", tk.END)

    def render_job_row(self, job_id):
        job = self.jobs[job_id]
        row = ctk.CTkFrame(self.scroll_frame, corner_radius=5, fg_color="#2b2b2b")
        row.pack(fill="x", pady=2, padx=2)
        job["row_frame"] = row  
        
        display_url = (job["url"][:40] + "...") if len(job["url"]) > 40 else job["url"]
        ctk.CTkLabel(row, text=display_url, anchor="w", width=250).pack(side="left", padx=5)
        
        # Display filenames if they exist
        filename1 = job.get("filename1", "")
        filename2 = job.get("filename2", "")
        display_text = f"{filename1}, {filename2}" if filename2 else filename1
        
        job["filename_label"] = ctk.CTkLabel(row, text=display_text, anchor="w", width=180, text_color="#f1c40f", font=("Arial", 12, "bold"))
        job["filename_label"].pack(side="left", padx=5)

        status_menu = ctk.CTkOptionMenu(row, variable=job["status_var"], values=list(self.status_colors.keys()), 
                                        command=lambda val, jid=job_id: self.on_manual_status_change(jid, val),
                                        width=130, fg_color=self.status_colors.get(job["status"], "gray"))
        status_menu.pack(side="left", padx=5)
        job["status_menu"] = status_menu
        
        ctk.CTkButton(row, text="🗑️", width=30, command=lambda: self.delete_single_job(job_id), fg_color="#e74c3c").pack(side="right", padx=2)
        ctk.CTkButton(row, text="🌐", width=30, command=lambda: self.open_url(job_id), fg_color="#34495e").pack(side="right", padx=2)
        ctk.CTkButton(row, text="📋", width=30, command=lambda: self.copy_url(job_id), fg_color="#34495e").pack(side="right", padx=2)
        ctk.CTkButton(row, text="Send", width=60, command=lambda: self.send_single_job(job_id), fg_color="#34495e").pack(side="right", padx=2)

    def copy_url(self, job_id):
        if job_id in self.jobs:
            url = self.jobs[job_id]["url"]
            self.clipboard_clear()
            self.clipboard_append(url)

    def open_url(self, job_id):
        if job_id in self.jobs:
            job = self.jobs[job_id]
            webbrowser.open(job["url"])
            if job.get("filename1"):
                self.clipboard_clear()
                self.clipboard_append(job["filename1"])

    def on_manual_status_change(self, job_id, new_status):
        self.update_job_status(job_id, new_status)

    def update_job_status(self, job_id, new_status):
        if job_id in self.jobs:
            self.jobs[job_id]["status"] = new_status
            self.jobs[job_id]["status_var"].set(new_status)
            self.jobs[job_id]["status_menu"].configure(fg_color=self.status_colors.get(new_status, "gray"))
            profile_name = self.jobs[job_id].get("profile_name", self.profile_var.get())
            self.save_profile_jobs(profile_name)

    def save_profile_jobs(self, profile_name):
        """Save ALL jobs for a specific profile on the current date"""
        profile_jobs = {jid: jdata for jid, jdata in self.jobs.items() 
                       if jdata.get("profile_name") == profile_name 
                       and jdata.get("date") == self.current_date}
        save_jobs_to_disk(profile_jobs, profile_name, self.current_date)
    
    def delete_single_job(self, job_id):
        if job_id in self.jobs:
            profile_name = self.jobs[job_id].get("profile_name", self.profile_var.get())
            if "row_frame" in self.jobs[job_id]:
                self.jobs[job_id]["row_frame"].destroy()
            del self.jobs[job_id]
            self.save_profile_jobs(profile_name)

    def delete_all_jobs(self):
        if not self.jobs:
            return
        if messagebox.askyesno("Confirm", "Are you sure you want to delete all jobs for this profile on this date?"):
            profile_name = self.profile_var.get()
            for job_data in list(self.jobs.values()):
                if job_data.get("profile_name") == profile_name and job_data.get("date") == self.current_date:
                    if "row_frame" in job_data:
                        job_data["row_frame"].destroy()
            self.jobs = {jid: jdata for jid, jdata in self.jobs.items() 
                        if not (jdata.get("profile_name") == profile_name and jdata.get("date") == self.current_date)}
            self.save_profile_jobs(profile_name)
            self.refresh_date_menu()

    def start_automation(self):
        self.is_running = True
        self.is_paused = False
        self.retry_round = 0
        self.start_btn.configure(state="disabled")
        self.pause_btn.configure(state="normal", text="⏸ Pause")
        self.stop_btn.configure(state="normal")
        
        for job_id, job in self.jobs.items():
            if job["status"] == "Pending":
                if self.generate_all_var.get():
                    job["profiles_to_process"] = [p["name"] for p in self.profiles if is_profile_complete(p)]
                else:
                    current_p = next((p for p in self.profiles if p["name"] == self.app_settings["current_profile"]), None)
                    job["profiles_to_process"] = [current_p["name"]] if current_p and is_profile_complete(current_p) else []
                    
        self.process_next_pending_job()

    def toggle_pause(self):
        self.is_paused = not self.is_paused
        self.pause_btn.configure(text="▶ Resume" if self.is_paused else "⏸ Pause", fg_color="#2ecc71" if self.is_paused else "#f39c12")
        if not self.is_paused: self.process_next_pending_job()

    def stop_automation(self):
        self.is_running = False
        self.is_paused = False
        self.retry_round = 0
        self.start_btn.configure(state="normal")
        self.pause_btn.configure(state="disabled", text="⏸ Pause")
        self.stop_btn.configure(state="disabled")

    def get_next_pending_job(self):
        for job_id, data in self.jobs.items():
            if data["status"] == "Pending": return job_id
        return None

    def process_next_pending_job(self):
        if not self.is_running or self.is_paused: return
        job_id = self.get_next_pending_job()
        if job_id:
            self.process_profile_for_job(job_id)
        else:
            if self.retry_round == 0:
                failed_jobs = [jid for jid, data in self.jobs.items() if data["status"] == "Failed"]
                if failed_jobs:
                    print(f"🔄 [Automation] Round 1 complete. Retrying {len(failed_jobs)} failed jobs in Round 2...")
                    for jid in failed_jobs: 
                        if self.generate_all_var.get():
                            self.jobs[jid]["profiles_to_process"] = [p["name"] for p in self.profiles if is_profile_complete(p)]
                        else:
                            current_p = next((p for p in self.profiles if p["name"] == self.app_settings["current_profile"]), None)
                            self.jobs[jid]["profiles_to_process"] = [current_p["name"]] if current_p and is_profile_complete(current_p) else []
                        
                        self.jobs[jid]["status"] = "Pending"
                        self.jobs[jid]["status_var"].set("Pending")
                        if "status_menu" in self.jobs[jid]:
                            self.jobs[jid]["status_menu"].configure(fg_color=self.status_colors.get("Pending", "gray"))
                        
                        profile_name = self.jobs[jid].get("profile_name", self.profile_var.get())
                        self.save_profile_jobs(profile_name)
                        
                    self.retry_round = 1
                    self.process_next_pending_job()
                else:
                    print("✅ [Automation] All jobs processed successfully.")
                    self.stop_automation()
            else:
                print("✅ [Automation] Round 2 complete. Ending automation process.")
                self.stop_automation()

    def process_profile_for_job(self, job_id):
        job = self.jobs[job_id]
        if not job.get("profiles_to_process"):
            if job["status"] == "Resume Received":
                self.update_job_status(job_id, "Done")
            else:
                self.update_job_status(job_id, "Failed")
            self.process_next_pending_job()
            return
        
        profile_name = job["profiles_to_process"].pop(0)
        job["current_profile"] = profile_name
        job_profile_map[job_id] = profile_name
        
        self.update_job_status(job_id, "Running")
        payload = {"type": "START_TASK", "jobId": job_id, "jobUrl": job["url"], "profileName": profile_name}
        ui_to_ws_queue.put(json.dumps(payload))

    def send_single_job(self, job_id):
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
                
            self.process_profile_for_job(job_id)

    def poll_queues(self):
        try:
            while True:
                message = ws_to_ui_queue.get_nowait()
                data = json.loads(message)
                msg_type = data.get("type")
                job_id = data.get("jobId")

                if msg_type == "TASK_COMPLETE":
                    resume_text = data.get("resumeText", "")
                    marker = "[FolderName]:"
                    
                    if marker not in resume_text:
                        print(f"❌ [Backend] Generation FAILED for Job {job_id}. Marker '{marker}' not found.")
                        self.update_job_status(job_id, "Failed")
                        self.process_next_pending_job()
                        continue
                    
                    idx = resume_text.index(marker) + len(marker)
                    remaining = resume_text[idx:].strip()
                    parts = re.split(r'[,\s]+', remaining)
                    parts = [p for p in parts if p]
                    
                    filename1 = parts[0] if len(parts) > 0 else ""
                    filename2 = parts[1] if len(parts) > 1 else ""
                    
                    self.jobs[job_id]["filename1"] = filename1
                    self.jobs[job_id]["filename2"] = filename2
                    
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
                        print(f"🚀 [Macro] Starting Word macro for profile '{current_profile_name}'...")
                        threading.Thread(target=self.run_macros_for_profiles, args=(job_id, resume_text, profiles_to_run), daemon=True).start()
                        
                        print(f"⚡ [Optimization] Sending next job while macro runs in background...")
                        self.process_next_pending_job()
                    else:
                        print("⚠️ [Macro] Skipping macro. Profile incomplete or missing resume text.")
                        self.update_job_status(job_id, "Failed")
                        self.process_next_pending_job()

                elif msg_type == "TASK_FAILED":
                    self.update_job_status(job_id, "Failed")
                    self.process_next_pending_job()
                    
                elif msg_type == "MACRO_SUCCESS":
                    if self.jobs[job_id].get("profiles_to_process"):
                        self.process_profile_for_job(job_id)
                    else:
                        self.update_job_status(job_id, "Done")
                    
                elif msg_type == "MACRO_FAILED":
                    self.update_job_status(job_id, "Failed")
                    
                elif msg_type == "EXTENSION_STATUS":
                    status = data.get("status")
                    self.header.configure(text=f"🚀 Job Automation Dashboard ({'🟢 Connected' if status == 'connected' else '🔴 Disconnected'})")
                    self.start_btn.configure(state="normal" if status == 'connected' else "disabled")
                        
                ws_to_ui_queue.task_done()
        except queue.Empty: pass
        self.after(100, self.poll_queues)

    def run_macros_for_profiles(self, job_id, resume_text, profiles):
        all_success = True
        for profile in profiles:
            success = run_word_macro(resume_text, profile["docm_path"], profile["macro_name"], profile["name"])
            if not success:
                all_success = False
        
        if all_success:
            ws_to_ui_queue.put(json.dumps({"type": "MACRO_SUCCESS", "jobId": job_id}))
        else:
            ws_to_ui_queue.put(json.dumps({"type": "MACRO_FAILED", "jobId": job_id}))

if __name__ == "__main__":
    app = JobAutomatorApp()
    app.mainloop()