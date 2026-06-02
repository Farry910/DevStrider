# Kids Coding Guide: Python, GitHub & Bash

Welcome! This guide will teach you three super useful skills that real programmers use every day. Let's go step by step.

---

## Table of Contents

1. [Bash Commands - Talking to Your Computer](#1-bash-commands)
2. [Python - Writing Your First Programs](#2-python)
3. [GitHub - Saving and Sharing Your Work](#3-github)

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

## Keep Learning

- **Python:** [python.org/about/gettingstarted](https://www.python.org/about/gettingstarted/)
- **GitHub:** [docs.github.com](https://docs.github.com)
- **Bash:** Search "bash beginner tutorial" on YouTube

> **Remember:** Every programmer was a beginner once. The most important thing is to keep experimenting and have fun!
