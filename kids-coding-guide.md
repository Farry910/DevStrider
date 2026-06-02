# Kids Coding Guide: Python, GitHub & Bash

Welcome! This guide will teach you three super useful skills that real programmers use every day. Let's go step by step.

---

## Table of Contents

1. [Bash Commands - Talking to Your Computer](#1-bash-commands)
   - [Bash: Level 2](#bash-level-2)
2. [Python - Writing Your First Programs](#2-python)
   - [Python: Level 2](#python-level-2)
3. [GitHub - Saving and Sharing Your Work](#3-github)
   - [GitHub: Level 2](#github-level-2)
4. [Bigger Project: A Daily Journal CLI](#bigger-project-a-daily-journal-cli)

---

## 1. Bash Commands

> **What is Bash?** Bash is a way to talk directly to your computer by typing commands. Instead of clicking with a mouse, you type instructions.

Open your **Terminal** (Mac/Linux) or **Git Bash** (Windows) to try these out.

### Navigating Around

| Command | What it does | Example |
|---------|-------------|---------|
| `pwd` | Shows where you are (Print Working Directory) | `pwd` |
| `ls` | Lists files and folders | `ls` |
| `cd` | Change Directory (move to a folder) | `cd Desktop` |
| `cd ..` | Go up one folder | `cd ..` |
| `cd ~` | Go to your home folder | `cd ~` |

**Try it:**
```bash
pwd
ls
cd Desktop
pwd
```

### Working with Files and Folders

| Command | What it does | Example |
|---------|-------------|---------|
| `mkdir` | Make a new folder | `mkdir my-project` |
| `touch` | Create an empty file | `touch hello.txt` |
| `cp` | Copy a file | `cp hello.txt hello2.txt` |
| `mv` | Move or rename a file | `mv hello.txt world.txt` |
| `rm` | Delete a file (careful!) | `rm world.txt` |
| `cat` | Show the contents of a file | `cat hello.txt` |

**Try it:**
```bash
mkdir my-first-folder
cd my-first-folder
touch my-file.txt
ls
cat my-file.txt
```

### Handy Tips
- Press **Tab** to auto-complete a file or folder name
- Press **Up Arrow** to repeat your last command
- Type `clear` to clean up the screen
- Press **Ctrl + C** to stop a command that is running

---

## Bash: Level 2

You've got the basics. Now let's chain commands and write real scripts.

### Wildcards (Patterns)

Match many files at once.

| Pattern | Matches |
|---------|---------|
| `*` | Anything (any number of characters) |
| `?` | Exactly one character |
| `*.txt` | Every `.txt` file |
| `photo?.jpg` | photo1.jpg, photoA.jpg |
| `notes_*.md` | notes_monday.md, notes_july.md |

```bash
ls *.py            # All Python files in this folder
rm temp_*.txt      # Delete every temp_*.txt file (careful!)
cp *.jpg backup/   # Copy every jpg into a folder named backup
```

### Pipes — Chain Commands Together

The `|` pipe sends the output of one command into the next.

```bash
ls | wc -l         # Count how many things are in this folder
cat poem.txt | head    # First 10 lines
ls -la | sort      # List files, then sort
```

### Redirects — Save Output to a File

```bash
ls > files.txt     # Write output INTO files.txt (overwrite)
ls >> files.txt    # APPEND to files.txt
echo "Hi mom" > note.txt
cat note.txt
```

### Useful New Commands

| Command | What it does |
|---------|--------------|
| `grep "word" file.txt` | Find every line containing "word" |
| `head -5 file.txt` | First 5 lines |
| `tail -5 file.txt` | Last 5 lines |
| `wc -l file.txt` | Count lines |
| `sort` | Sort lines alphabetically |
| `uniq` | Remove duplicate adjacent lines |
| `find . -name "*.py"` | Find every `.py` file under here |
| `curl https://example.com` | Download a page or API response |

**Real combos with pipes:**

```bash
# How many Python files are in this project?
find . -name "*.py" | wc -l

# Show every line containing TODO across every .py file
grep "TODO" *.py

# Top 5 most common words in a file
cat poem.txt | tr ' ' '\n' | sort | uniq -c | sort -rn | head -5
```

### Environment Variables

Variables any command can read.

```bash
export NAME="Alex"
echo "Hello, $NAME"

export AGE=10
echo "I am $AGE years old"

echo $HOME    # Your home folder
echo $PATH    # Where bash looks for commands
env           # See all env vars
```

### Writing Your First Shell Script

A shell script is a text file with commands inside.

**Create `hello.sh`:**

```bash
#!/bin/bash
# The "#!/bin/bash" line tells the OS to run this with bash.

echo "What's your name?"
read name
echo "Welcome, $name!"

# Loop
for i in 1 2 3; do
    echo "Counting: $i"
done

# If/else (note the spaces around the brackets — they matter!)
if [ "$name" = "Alex" ]; then
    echo "Cool name!"
else
    echo "Nice to meet you."
fi
```

**Run it:**

```bash
chmod +x hello.sh   # Make it executable (once)
./hello.sh          # Run!
```

### Aliases — Short Names for Long Commands

Add to `~/.bashrc` (or `~/.zshrc` on a Mac):

```bash
alias ll='ls -la'
alias gs='git status'
alias ..='cd ..'
```

Open a new terminal — now typing `ll` runs `ls -la`.

### Mini Challenge

Write a script `setup.sh` that:
1. Asks the user for a project name
2. Creates a folder with that name
3. Inside it, creates `README.md` containing the line `# <project name>`
4. Creates an empty `main.py`
5. Lists everything in the new folder

---

## 2. Python

> **What is Python?** Python is a friendly programming language. It reads almost like English, so it is great for beginners!

Make sure Python is installed by typing `python3 --version` in your terminal.

### 2.1 Printing Things

```python
print("Hello, World!")
print("My name is Alex")
print(2 + 3)
```

**Output:**
```
Hello, World!
My name is Alex
5
```

### 2.2 Variables (Storing Information)

A variable is like a labeled box that holds a value.

```python
name = "Alex"
age = 10
favorite_number = 7

print(name)
print(age)
print(favorite_number)
```

### 2.3 Data Types

```python
# Text (called a "string")
greeting = "Hello!"

# Whole number (called an "integer")
apples = 5

# Decimal number (called a "float")
price = 1.99

# True or False (called a "boolean")
is_sunny = True
is_raining = False
```

> Lines that start with `#` are **comments** — Python ignores them. They are notes for humans!

### 2.4 Math Operations

```python
print(5 + 3)    # Addition -> 8
print(10 - 4)   # Subtraction -> 6
print(3 * 4)    # Multiplication -> 12
print(15 / 3)   # Division -> 5.0
print(10 % 3)   # Remainder -> 1
print(2 ** 8)   # Power (2 to the 8th) -> 256
```

### 2.5 Getting Input from the User

```python
name = input("What is your name? ")
print("Nice to meet you, " + name + "!")
```

### 2.6 If / Else (Making Decisions)

```python
age = int(input("How old are you? "))

if age >= 13:
    print("You can watch PG-13 movies!")
else:
    print("Maybe wait a few more years.")
```

**More conditions:**

```python
score = 85

if score >= 90:
    print("A - Excellent!")
elif score >= 80:
    print("B - Great job!")
elif score >= 70:
    print("C - Good effort!")
else:
    print("Keep practicing!")
```

### 2.7 Loops (Repeating Things)

**For loop** — repeat a set number of times:

```python
for i in range(5):
    print("Count:", i)
```

**Output:**
```
Count: 0
Count: 1
Count: 2
Count: 3
Count: 4
```

**While loop** — repeat until something changes:

```python
count = 0
while count < 3:
    print("Hello!")
    count = count + 1
```

### 2.8 Lists (Groups of Things)

```python
fruits = ["apple", "banana", "cherry"]

print(fruits[0])   # apple (counting starts at 0!)
print(fruits[1])   # banana
print(fruits[2])   # cherry

fruits.append("mango")   # Add to the end
print(fruits)

print(len(fruits))        # How many items? -> 4
```

**Loop through a list:**

```python
colors = ["red", "green", "blue"]

for color in colors:
    print("I like", color)
```

### 2.9 Functions (Reusable Blocks of Code)

```python
def greet(name):
    print("Hello, " + name + "!")

greet("Alex")
greet("Sam")
greet("Jordan")
```

**A function that gives back a result:**

```python
def add(a, b):
    return a + b

result = add(3, 5)
print(result)   # 8
```

### 2.10 Mini Project: Number Guessing Game

```python
secret = 7

guess = int(input("Guess a number between 1 and 10: "))

if guess == secret:
    print("You got it! Amazing!")
elif guess < secret:
    print("Too low! Try again.")
else:
    print("Too high! Try again.")
```

---

## Python: Level 2

You can print, loop, and use lists. Now you can start building real things.

### 2.11 f-strings — A Better Way to Print

The cleanest way to mix variables into text:

```python
name = "Alex"
age = 10

# Old way
print("Hello, " + name + "! You are " + str(age))

# f-string way (way nicer)
print(f"Hello, {name}! You are {age}")

# You can do math inside the braces
print(f"In 5 years you'll be {age + 5}")

# Format numbers
price = 3.14159
print(f"Pi is roughly {price:.2f}")    # 3.14
```

### 2.12 Useful String Methods

```python
text = "  Hello World  "

print(text.upper())       # "  HELLO WORLD  "
print(text.lower())       # "  hello world  "
print(text.strip())       # "Hello World"  (no edge spaces)
print(text.replace("World", "Python"))   # "  Hello Python  "
print(text.split())       # ["Hello", "World"]
print("a,b,c".split(",")) # ["a", "b", "c"]
print(",".join(["a", "b", "c"]))  # "a,b,c"
print(len(text))          # 15
print("hello".startswith("he"))   # True
```

### 2.13 Dictionaries — Lookups by Name

A dictionary maps a **key** to a **value**, like a real dictionary maps words to definitions.

```python
ages = {
    "Alex": 10,
    "Sam": 12,
    "Jordan": 9
}

print(ages["Alex"])       # 10

# Add or change
ages["Robin"] = 11

# Loop through it
for name, age in ages.items():
    print(f"{name} is {age}")

# Check if a key is there
if "Alex" in ages:
    print("Alex is in the dictionary!")

# Safe get (returns None if missing)
print(ages.get("Pat"))           # None
print(ages.get("Pat", "unknown"))  # "unknown"
```

### 2.14 List Comprehensions — Build Lists Fast

Short syntax for building a list from another list.

```python
# Long way:
squares = []
for n in range(10):
    squares.append(n * n)

# List-comprehension way:
squares = [n * n for n in range(10)]
print(squares)
# [0, 1, 4, 9, 16, 25, 36, 49, 64, 81]

# With a filter
evens = [n for n in range(20) if n % 2 == 0]
print(evens)   # [0, 2, 4, 6, 8, 10, 12, 14, 16, 18]

# Transform a list of strings
names = ["alex", "SAM", " Jordan "]
clean = [n.strip().title() for n in names]
print(clean)   # ['Alex', 'Sam', 'Jordan']
```

### 2.15 Working With Files

```python
# Write to a file
with open("notes.txt", "w") as f:
    f.write("My first note\n")
    f.write("My second note\n")

# Read it all back
with open("notes.txt", "r") as f:
    content = f.read()
    print(content)

# Read line by line
with open("notes.txt", "r") as f:
    for line in f:
        print("LINE:", line.strip())

# Append (don't overwrite)
with open("notes.txt", "a") as f:
    f.write("A new note\n")
```

> The `with` block automatically closes the file when you're done. Always use it.

### 2.16 Modules — Borrowing Tools

Python comes with lots of built-in modules.

```python
import random

print(random.randint(1, 100))                  # Random number 1-100
print(random.choice(["pizza", "burger", "sushi"]))   # Random pick
random.shuffle([1, 2, 3, 4, 5])                # Mix up a list

import datetime

today = datetime.date.today()
print(today)                                    # 2026-06-02
print(f"It's the year {today.year}")
now = datetime.datetime.now()
print(now.strftime("%I:%M %p"))                # "02:47 PM"

import math

print(math.pi)                                  # 3.14159...
print(math.sqrt(64))                            # 8.0
print(math.floor(4.9))                          # 4
print(math.ceil(4.1))                           # 5
```

### 2.17 Handling Errors With try/except

What if the user types garbage?

```python
try:
    age = int(input("How old are you? "))
    print(f"You are {age}.")
except ValueError:
    print("That wasn't a number. Try again next time.")
```

You can catch different kinds of errors separately:

```python
try:
    with open("missing.txt") as f:
        data = f.read()
except FileNotFoundError:
    print("That file doesn't exist.")
except PermissionError:
    print("Not allowed to open that file.")
```

### 2.18 Classes — Your Own Blueprint

A **class** is a blueprint. An **object** (or **instance**) is one thing built from it.

```python
class Pet:
    def __init__(self, name, species):
        self.name = name
        self.species = species
        self.hunger = 5

    def feed(self):
        self.hunger = max(0, self.hunger - 3)
        print(f"{self.name} the {self.species} was fed. Hunger: {self.hunger}")

    def play(self):
        self.hunger += 1
        print(f"{self.name} loves playing!")

    def __str__(self):
        # Lets you print(pet) and get a nice description
        return f"<{self.species} named {self.name}, hunger {self.hunger}>"

fido = Pet("Fido", "dog")
whiskers = Pet("Whiskers", "cat")

fido.feed()
fido.play()
print(whiskers)
```

### 2.19 JSON — Save Structured Data to a File

JSON looks just like Python dicts and lists, but it's a text format any language can read.

```python
import json

high_scores = {
    "Alex": 1200,
    "Sam": 950,
    "Jordan": 1500
}

# Save
with open("scores.json", "w") as f:
    json.dump(high_scores, f, indent=2)

# Load later
with open("scores.json", "r") as f:
    loaded = json.load(f)

print(loaded["Jordan"])    # 1500
```

### 2.20 Installing Extra Libraries With pip

Python has thousands of free libraries. Install them with `pip`:

```bash
pip install requests
```

Then use them:

```python
import requests

response = requests.get("https://api.github.com/users/octocat")
data = response.json()
print(data["name"])           # The Octocat
print(data["public_repos"])   # How many public repos they have
```

> Pro tip: create a **virtual environment** for each project so libraries don't clash:
> `python -m venv venv` then `source venv/bin/activate` (Linux/Mac) or `venv\Scripts\activate` (Windows).

### 2.21 Mini Project: Quiz Game With Saved Scores

```python
import json
import random
from pathlib import Path

QUESTIONS = [
    {"q": "What is 7 x 8?", "a": "56"},
    {"q": "Capital of France?", "a": "Paris"},
    {"q": "How many sides does a hexagon have?", "a": "6"},
    {"q": "Mix blue and yellow — what color?", "a": "green"},
    {"q": "What planet is known as the Red Planet?", "a": "Mars"},
]

SCORE_FILE = Path("scores.json")

def load_scores() -> dict:
    if SCORE_FILE.exists():
        with open(SCORE_FILE) as f:
            return json.load(f)
    return {}

def save_scores(scores: dict) -> None:
    with open(SCORE_FILE, "w") as f:
        json.dump(scores, f, indent=2)

def play() -> None:
    name = input("What is your name? ").strip()
    questions = QUESTIONS.copy()
    random.shuffle(questions)
    score = 0

    for item in questions:
        answer = input(item["q"] + " ").strip().lower()
        if answer == item["a"].lower():
            print("Correct!")
            score += 1
        else:
            print(f"Not quite. Answer: {item['a']}.")

    print(f"\n{name}, final score: {score}/{len(questions)}")

    scores = load_scores()
    if score > scores.get(name, 0):
        scores[name] = score
        save_scores(scores)
        print("New personal best — saved!")

    print("\nAll-time leaderboard:")
    leaderboard = sorted(scores.items(), key=lambda kv: -kv[1])
    for player, best in leaderboard:
        print(f"  {player}: {best}")

play()
```

This uses everything from Level 2: f-strings, dictionaries, list comprehensions (via `sorted`), file I/O, JSON, `random`, type hints, and the `pathlib` module.

---

## 3. GitHub

> **What is GitHub?** GitHub is like a magical save system for your code. It remembers every change you ever made, and lets you share your work with others.

### Key Words to Know

| Word | Meaning |
|------|---------|
| **Repository (repo)** | A folder that GitHub keeps track of |
| **Commit** | A saved snapshot of your work |
| **Branch** | A separate copy to try new ideas safely |
| **Push** | Send your changes to GitHub |
| **Pull** | Get the latest changes from GitHub |
| **Clone** | Download a copy of a repo to your computer |

### 3.1 First-Time Setup

Open your terminal and run these once:

```bash
git config --global user.name "Your Name"
git config --global user.email "your@email.com"
```

### 3.2 Starting a New Project

```bash
# 1. Create a folder and go inside it
mkdir my-cool-project
cd my-cool-project

# 2. Tell Git to start tracking this folder
git init

# 3. Create a file
touch hello.py

# 4. Write some code (open hello.py in your editor and save it)

# 5. Stage your file (tell Git "I want to save this")
git add hello.py

# 6. Commit (save a snapshot with a message)
git commit -m "My first commit!"
```

### 3.3 The Everyday Workflow

```
Write code  -->  git add  -->  git commit  -->  git push
```

```bash
# See what changed
git status

# Stage all changed files
git add .

# Save a snapshot
git commit -m "Added a cool new feature"

# Send to GitHub
git push
```

### 3.4 Connecting to GitHub

1. Go to **github.com** and create a free account
2. Click **New Repository** and give it a name
3. Copy the commands GitHub shows you and paste them in your terminal

```bash
git remote add origin https://github.com/yourname/my-cool-project.git
git push -u origin main
```

### 3.5 Downloading Someone Else's Project

```bash
git clone https://github.com/someone/their-project.git
cd their-project
```

### 3.6 Checking History

```bash
# See all your commits
git log

# See a shorter version
git log --oneline
```

### 3.7 Common Git Commands Cheat Sheet

| Command | What it does |
|---------|-------------|
| `git init` | Start tracking a folder |
| `git status` | See what has changed |
| `git add .` | Stage all changes |
| `git commit -m "message"` | Save a snapshot |
| `git push` | Upload to GitHub |
| `git pull` | Download latest changes |
| `git clone <url>` | Copy a repo to your computer |
| `git log --oneline` | See commit history |

---

## GitHub: Level 2

Time to work the way real developers do.

### 3.8 Branches — Try New Ideas Safely

A branch is a parallel copy of your project where you can experiment without breaking your "main" version.

```bash
# Create a new branch and switch to it
git checkout -b new-feature

# See which branch you're on (and all branches)
git branch

# Make some changes, commit them on this branch
echo "experimental code" >> game.py
git add game.py
git commit -m "Try a new idea"

# Switch back to main
git checkout main

# Merge your work back in when you're happy
git merge new-feature

# Delete the branch once you're done
git branch -d new-feature
```

> Newer Git also has `git switch`:
> `git switch -c new-feature` creates and switches in one step.

### 3.9 .gitignore — Files Git Should Ignore

Some files you never want in GitHub: passwords, junk files, compiled outputs.

Create a file called `.gitignore` and list patterns:

```gitignore
# Personal config
.env
secrets.txt

# Python junk
__pycache__/
*.pyc

# OS junk
.DS_Store
Thumbs.db

# Editor stuff
.vscode/
.idea/
```

After saving `.gitignore`, those files won't show up in `git status` anymore.

### 3.10 git diff — What Changed?

```bash
git diff               # Show changes you haven't staged yet
git diff --staged      # Show changes you've staged but not committed
git diff main feature  # Compare two branches
```

### 3.11 Undoing Things (Safely)

```bash
# Discard unstaged changes to a file — can't be undone, careful!
git restore my-file.py

# Unstage (keep changes, just move out of staging)
git restore --staged my-file.py

# Forgot to add something to the last commit?
git add forgotten-file.py
git commit --amend --no-edit

# Made a commit you regret? Create a NEW commit that reverses it:
git revert HEAD
```

> Rule of thumb: **prefer `git revert` over `git reset --hard`**. Revert keeps your history honest; reset throws work away.

### 3.12 Merge Conflicts — Two People Changed the Same Line

When Git can't auto-merge, your file looks like this:

```
<<<<<<< HEAD
print("Hello from main")
=======
print("Hello from feature branch")
>>>>>>> feature
```

To fix it:

1. Open the file in your editor
2. Pick which version (or combine them)
3. Delete the `<<<<<<<`, `=======`, and `>>>>>>>` markers
4. `git add` the file
5. `git commit` to finish the merge

### 3.13 Pull Requests — Sharing Work for Review

A pull request (PR) is how you propose changes on GitHub.

1. Push your branch: `git push -u origin new-feature`
2. On GitHub you'll see a **Compare & pull request** button — click it
3. Write a clear title and description: *what does this PR do, and why?*
4. Click **Create pull request**
5. A reviewer (or future you) leaves comments, then clicks **Merge**

### 3.14 README.md — Tell People What Your Project Does

Every good repo has a `README.md` at its root. Markdown is a simple way to format text.

````markdown
# My Cool Game

A number guessing game I made while learning Python.

## How to play

1. Run `python game.py`
2. Guess a number from 1 to 10
3. Try to get it in 3 tries!

## How to run

```
python game.py
```

## What I learned

- Using `input()` and `int()`
- `if / elif / else`
- Loops with `while`
````

### 3.15 GitHub Issues — Your Project's TODO List

Click the **Issues** tab on any GitHub repo to track tasks, bugs, and ideas.

- **New Issue** → short summary as title ("Add a 'play again' option")
- Description with details
- Close it when done

### 3.16 Forking — Building on Someone Else's Project

A **fork** is your own copy of someone else's repo.

1. Click **Fork** (top right of any repo)
2. You now have `github.com/yourname/the-project`
3. `git clone` your fork
4. Make changes, commit, push
5. If your changes are great, open a **pull request** back to the original

### 3.17 Updated Cheat Sheet

| Command | What it does |
|---------|--------------|
| `git checkout -b name` | Create and switch to a new branch |
| `git switch name` | Switch to an existing branch |
| `git branch` | List branches |
| `git merge name` | Merge a branch into the current one |
| `git diff` | See unstaged changes |
| `git restore file` | Discard local changes to a file |
| `git revert HEAD` | Undo the last commit safely |
| `git commit --amend` | Edit the last commit |
| `git push -u origin branch` | Push a new branch to GitHub |
| `git fetch` | Pull updates without merging |

---

## Putting It All Together: Your First Real Project

1. Create a folder called `guessing-game`
2. Go inside it and run `git init`
3. Create a file called `game.py`
4. Paste the guessing game code from section 2.10
5. Stage and commit it:
   ```bash
   git add game.py
   git commit -m "Add guessing game"
   ```
6. Create a repo on GitHub and push it up
7. Share the link with a friend!

---

## Bigger Project: A Daily Journal CLI

Let's build something real using everything from Level 2.

**What it does:** a command-line tool you can run any day to write a short journal entry, save it to disk, list past entries, and search them.

### Step 1: Create the project

```bash
mkdir my-journal
cd my-journal
git init
touch journal.py README.md .gitignore
```

### Step 2: Fill in `.gitignore`

```gitignore
__pycache__/
*.pyc
.DS_Store
entries.json
```

> We ignore `entries.json` because it's *your* personal data — no need to push it to GitHub.

### Step 3: Write `journal.py`

```python
import json
import datetime
import sys
from pathlib import Path

JOURNAL_FILE = Path("entries.json")

class Entry:
    def __init__(self, text: str, when: str | None = None):
        self.text = text
        self.when = when or datetime.datetime.now().isoformat()

    def to_dict(self) -> dict:
        return {"text": self.text, "when": self.when}

    @property
    def date(self) -> str:
        return self.when.split("T")[0]

def load_entries() -> list[Entry]:
    if JOURNAL_FILE.exists():
        with open(JOURNAL_FILE) as f:
            return [Entry(d["text"], d["when"]) for d in json.load(f)]
    return []

def save_entries(entries: list[Entry]) -> None:
    with open(JOURNAL_FILE, "w") as f:
        json.dump([e.to_dict() for e in entries], f, indent=2)

def add_entry() -> None:
    text = input("What happened today? ").strip()
    if not text:
        print("Empty entry, skipping.")
        return
    entries = load_entries()
    entries.append(Entry(text))
    save_entries(entries)
    print(f"Saved. You have {len(entries)} entries now.")

def list_entries() -> None:
    entries = load_entries()
    if not entries:
        print("No entries yet. Write your first one!")
        return
    for e in entries[-10:]:
        print(f"[{e.date}] {e.text}")

def search_entries() -> None:
    word = input("Search for: ").strip().lower()
    matches = [e for e in load_entries() if word in e.text.lower()]
    print(f"Found {len(matches)} entries:")
    for e in matches:
        print(f"  [{e.date}] {e.text}")

def main() -> None:
    print("Daily Journal")
    print("  1) Add an entry")
    print("  2) List recent entries")
    print("  3) Search")
    choice = input("Pick (1/2/3): ").strip()
    actions = {"1": add_entry, "2": list_entries, "3": search_entries}
    action = actions.get(choice)
    if action is None:
        print("Unknown choice.")
        sys.exit(1)
    action()

if __name__ == "__main__":
    main()
```

> `if __name__ == "__main__":` is a Python idiom — it means "only run this when the file is executed directly, not when it's imported." Use it for any script that has both library code and CLI behavior.

### Step 4: Test it

```bash
python journal.py
# Pick 1, add some entries
# Pick 2 to see them
# Pick 3 to search
```

### Step 5: Write a `README.md`

````markdown
# My Daily Journal

A tiny Python CLI to write and search journal entries.

## Run

```
python journal.py
```

## Features

- Add a new entry
- List the 10 most recent
- Search by keyword

## What I learned

- Classes with `__init__`
- Reading and writing JSON
- Working with dates (`datetime`)
- List comprehensions for filtering
- The `if __name__ == "__main__"` idiom
````

### Step 6: Put it on GitHub

```bash
git add journal.py README.md .gitignore
git commit -m "First version of the journal"

# Create the repo on github.com, then:
git remote add origin https://github.com/yourname/my-journal.git
git push -u origin main
```

### Step 7: Try a branch

Want to add an "edit an entry" feature? Don't break what works!

```bash
git checkout -b edit-feature

# ... write new code ...

git add journal.py
git commit -m "Add edit feature"
git push -u origin edit-feature
```

On GitHub, open a pull request from `edit-feature` to `main`. Review your own diff. Click **Merge** when you're happy.

### Step 8 (challenge): Make it even better

Pick one and try:
- Add a `--quick "<text>"` mode that adds an entry without showing the menu
- Sort search results by date (newest or oldest first)
- Add **tags** to entries (e.g. `#school`, `#football`) and let you search by tag
- Show a count of entries per month
- Replace `input()` with [`argparse`](https://docs.python.org/3/library/argparse.html) so the whole thing runs from command-line flags

---

## Keep Learning

**Python (next steps):**
- [Automate the Boring Stuff with Python](https://automatetheboringstuff.com/) — free online book, perfect for after this guide
- [Python standard library overview](https://docs.python.org/3/library/) — every batteries-included module
- [Real Python](https://realpython.com/) — hundreds of tutorials, search by topic

**GitHub (next steps):**
- [docs.github.com](https://docs.github.com)
- [Learn Git Branching](https://learngitbranching.js.org/) — interactive game that teaches branching and merging
- [GitHub Skills](https://skills.github.com/) — hands-on courses you do inside GitHub itself

**Bash (next steps):**
- [explainshell.com](https://explainshell.com/) — paste any command, get a breakdown of every flag
- [Bash Guide](https://mywiki.wooledge.org/BashGuide) — clear, well-structured next-level reference
- [The Missing Semester of Your CS Education](https://missing.csail.mit.edu/) — MIT's free course on the dev tools nobody teaches you

> **Remember:** Every programmer was a beginner once. The most important things are to keep experimenting, read other people's code, and build things you actually want to use.
