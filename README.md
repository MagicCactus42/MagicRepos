# MagicRepos

A Git-like distributed version control system written from scratch in C# targeting .NET 10. MagicRepos implements its own binary object model, content-addressable storage, staging area, branching, diffing, and a client-server protocol designed for SSH transport.

## Table of Contents

- [Architecture](#architecture)
- [Project Structure](#project-structure)
- [Building](#building)
- [Command Reference](#command-reference)
  - [Repository](#repository)
  - [Staging & Committing](#staging--committing)
  - [Viewing Changes](#viewing-changes)
  - [Branching](#branching)
  - [Resetting](#resetting)
  - [Remotes & Networking](#remotes--networking)
  - [Pull Requests](#pull-requests)
- [Client Setup](#client-setup)
  - [Windows](#windows-client-setup)
  - [Linux / macOS](#linux--macos-client-setup)
- [Server Setup: Proxmox LXC Step-by-Step](#server-setup-proxmox-lxc-step-by-step)
  - [Container Specs](#container-specs)
  - [Step 1: Create the LXC Container](#step-1-create-the-lxc-container)
  - [Step 2: Enable TUN Device for Tailscale](#step-2-enable-tun-device-for-tailscale)
  - [Step 3: Base System Setup](#step-3-base-system-setup)
  - [Step 4: Install Tailscale](#step-4-install-tailscale)
  - [Step 5: Create the Service User](#step-5-create-the-service-user)
  - [Step 6: Build and Deploy the Server Binary](#step-6-build-and-deploy-the-server-binary)
  - [Step 7: Configure OpenSSH with ForceCommand](#step-7-configure-openssh-with-forcecommand)
  - [Step 8: Generate and Install SSH Key](#step-8-generate-and-install-ssh-key)
  - [Step 9: Verify Everything Works](#step-9-verify-everything-works)
- [Personalization](#personalization)
  - [Changing the Tailscale Hostname](#changing-the-tailscale-hostname)
  - [Changing the SSH Service User](#changing-the-ssh-service-user)
  - [Changing the Data Directory](#changing-the-data-directory)
  - [Choosing Your Username](#choosing-your-username)
  - [Adding More Users / Machines](#adding-more-users--machines)
- [Access Control](#access-control)
  - [Rules](#rules)
  - [Managing Collaborators](#managing-collaborators)
  - [Admin Commands](#admin-commands)
- [End-to-End Workflow](#end-to-end-workflow)
  - [Solo Developer](#solo-developer)
  - [Team Workflow (Alice & Bob)](#team-workflow-alice--bob)
- [Troubleshooting](#troubleshooting)
- [Internal Design](#internal-design)
  - [Object Model](#object-model)
  - [Object Storage](#object-storage)
  - [Index (Staging Area)](#index-staging-area)
  - [References](#references)
  - [Diff Engine](#diff-engine)
  - [Ignore Rules](#ignore-rules)
- [Wire Protocol](#wire-protocol)
- [Testing](#testing)
- [Dependencies](#dependencies)
- [Roadmap](#roadmap)

---

## Architecture

```
+------------------+       SSH / stdin+stdout       +------------------+
|  MagicRepos.Cli  | -------------------------------->| MagicRepos.Server|
|  (user machine)  |    Binary framed protocol      |  (Proxmox LXC)  |
+--------+---------+                                +--------+---------+
         |                                                   |
   +-----v------+                                    +-------v--------+
   |MagicRepos.  |                                    |MagicRepos.     |
   |   Core      |                                    |   Core         |
   |(local repo) |                                    |(bare repos)    |
   +-------------+                                    +----------------+
```

| Assembly | Role |
|---|---|
| **MagicRepos.Core** | All VCS logic: objects, storage, index, refs, diff, ignore rules, remote client, and the `Repository` facade. Zero external dependencies. |
| **MagicRepos.Cli** | Console application exposing all commands via `System.CommandLine`. Colored output with `Spectre.Console`. |
| **MagicRepos.Protocol** | Wire protocol types serialized with MessagePack. Shared between client and server. |
| **MagicRepos.Server** | Server-side daemon invoked by OpenSSH `ForceCommand`. Manages bare repositories and pull requests. |

---

## Project Structure

```
MagicRepos.sln
src/
  MagicRepos.Core/
    Objects/
      ObjectId.cs             SHA-256 hash value type (32 bytes)
      ObjectType.cs           Blob | Tree | Commit enum
      BlobObject.cs           Raw file content
      TreeObject.cs           Sorted directory listing
      TreeEntry.cs            Single tree entry (mode, name, hash)
      CommitObject.cs         Tree + parents + author + message
      Signature.cs            Name + email + timestamp
    Storage/
      ObjectSerializer.cs     Header framing + DEFLATE compression
      ObjectStore.cs          Loose object read/write on disk
      IndexFile.cs            Binary staging area (MRIX format)
    Refs/
      RefStore.cs             HEAD, branches, tags, generic refs
    Config/
      RepoConfig.cs           INI-style configuration file
    Diff/
      DiffEngine.cs           Myers O(ND) diff algorithm
    Ignore/
      IgnoreRuleSet.cs        .magicignore glob pattern parser
    Remote/
      RemoteClient.cs         SSH transport client (push/pull/clone)
    WorkingTree.cs            Filesystem scanner with ignore support
    Repository.cs             Main facade orchestrating all operations
  MagicRepos.Cli/
    Program.cs                CLI entry point (17 commands)
  MagicRepos.Protocol/
    Messages/
      MessageType.cs          Enum of all wire message types
      ProtocolMessages.cs     MessagePack-annotated message classes
    MessageSerializer.cs      Frame encoding/decoding
  MagicRepos.Server/
    AccessControl.cs          Namespace-based access control + permissions.json
    BareRepository.cs         Server-side bare repository
    ServerRepositoryManager.cs Multi-repo management
    SessionHandler.cs         Push/pull/PR session dispatch with auth checks
    PullRequestStore.cs       JSON-backed PR storage
    Program.cs                Server entry point + admin commands
tests/
  MagicRepos.Core.Tests/      132 unit + integration tests
  MagicRepos.Protocol.Tests/  8 protocol serialization tests
```

---

## Building

Requires **.NET 10 SDK**.

```bash
# Build everything
dotnet build

# Run all tests
dotnet test

# Publish server (Linux x64, self-contained — no runtime needed on server)
dotnet publish src/MagicRepos.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/server

# Publish CLI — Windows
dotnet publish src/MagicRepos.Cli -c Release -o ./publish/cli

# Publish CLI — Linux
dotnet publish src/MagicRepos.Cli -c Release -r linux-x64 --self-contained true -o ./publish/cli-linux
```

---

## Command Reference

### Repository

| Command | Description |
|---|---|
| `magicrepos init` | Initialize a new repository in the current directory |
| `magicrepos init /path/to/project` | Initialize a new repository at a specific path |

Creates a `.magicrepos/` directory with: `objects/`, `refs/heads/`, `refs/tags/`, `refs/remotes/`, `HEAD` (pointing to `refs/heads/main`), and a default `config` file.

### Staging & Committing

| Command | Description |
|---|---|
| `magicrepos add <file>` | Stage a single file |
| `magicrepos add -A` | Stage all changes (new, modified, deleted) |
| `magicrepos commit -m "message"` | Create a commit with a message |
| `magicrepos commit -m "msg" --author "Name <email>"` | Commit with explicit author |

### Viewing Changes

| Command | Description |
|---|---|
| `magicrepos status` | Show staged, unstaged, and untracked files |
| `magicrepos log` | Show commit history (default: last 10) |
| `magicrepos log -n 50` | Show last 50 commits |
| `magicrepos diff` | Show unstaged changes (working tree vs index) |
| `magicrepos diff --staged` | Show staged changes (index vs HEAD) |

Status output uses color coding: **green** = staged, **red** = unstaged, **grey** = untracked.
Diff output uses unified diff format: **green** = additions, **red** = deletions, **cyan** = hunk headers.

### Branching

| Command | Description |
|---|---|
| `magicrepos branch <name>` | Create a new branch at current HEAD |
| `magicrepos branch -l` | List all branches (`*` marks current) |
| `magicrepos branch -d <name>` | Delete a branch |
| `magicrepos checkout <branch>` | Switch to a branch |

### Resetting

| Command | Description |
|---|---|
| `magicrepos reset HEAD~1 --soft` | Move HEAD only, keep index and working tree |
| `magicrepos reset HEAD~1` | Move HEAD and reset index (mixed, default) |
| `magicrepos reset HEAD --hard` | Move HEAD, reset index, restore working tree |

### Remotes & Networking

| Command | Description |
|---|---|
| `magicrepos remote add <name> <url>` | Add a remote |
| `magicrepos remote list` | List configured remotes |
| `magicrepos push` | Push to default remote (`origin`) |
| `magicrepos push <remote>` | Push to a named remote |
| `magicrepos pull` | Pull from default remote (`origin`) |
| `magicrepos pull <remote>` | Pull from a named remote |
| `magicrepos clone <url>` | Clone a remote repository |
| `magicrepos clone <url> <dir>` | Clone into a specific directory |

**Remote URL format:**

```
<ssh-user>@<hostname>:<your-username>/<reponame>
```

Examples:
```
magicrepos@magic-repos:magiccactus/MagicRepos
magicrepos@100.91.61.66:alice/my-project
myserver@my-host:bob/cool-app
```

- `<ssh-user>` — the SSH service user on the server (default: `magicrepos`, you can change this)
- `<hostname>` — Tailscale hostname or IP address
- `<your-username>/<reponame>` — this is NOT a filesystem path. The server maps it to its data directory automatically

### Pull Requests

| Command | Description |
|---|---|
| `magicrepos pr create` | Create a pull request |
| `magicrepos pr list` | List pull requests |
| `magicrepos pr review <number>` | Review a pull request |
| `magicrepos pr merge <number>` | Merge a pull request |

---

## Client Setup

### Windows Client Setup

**Option A: Add to PATH (recommended)**

```powershell
# Build the CLI
dotnet publish src/MagicRepos.Cli -c Release -o ./publish/cli

# Create a bin directory if you don't have one
mkdir C:\Users\<YOUR_WINDOWS_USER>\bin

# Copy the executable
copy .\publish\cli\MagicRepos.Cli.exe C:\Users\<YOUR_WINDOWS_USER>\bin\magicrepos.exe

# Add to PATH permanently (run in PowerShell as admin)
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";C:\Users\<YOUR_WINDOWS_USER>\bin", "User")

# Restart your terminal, then:
magicrepos --help
```

Replace `<YOUR_WINDOWS_USER>` with your Windows username (e.g. `Micha`).

**Option B: Bash alias (Git Bash / MSYS2)**

If you use Git Bash or MSYS2:

```bash
# Build
dotnet publish src/MagicRepos.Cli -c Release -o ./publish/cli

# Add alias pointing directly to the built executable
echo 'alias magicrepos="~/RiderProjects/MagicRepos/publish/cli/MagicRepos.Cli.exe"' >> ~/.bashrc
source ~/.bashrc

# Test
magicrepos --help
```

Adjust the path if your project is elsewhere.

**Option C: Run directly without alias**

```bash
./publish/cli/MagicRepos.Cli.exe init
./publish/cli/MagicRepos.Cli.exe add -A
./publish/cli/MagicRepos.Cli.exe commit -m "my commit"
```

### Linux / macOS Client Setup

```bash
# Build self-contained binary
dotnet publish src/MagicRepos.Cli -c Release -r linux-x64 --self-contained true -o ./publish/cli-linux

# For macOS use: -r osx-x64 or -r osx-arm64 (Apple Silicon)

# Copy to PATH
sudo cp ./publish/cli-linux/MagicRepos.Cli /usr/local/bin/magicrepos
sudo chmod +x /usr/local/bin/magicrepos

# Test
magicrepos --help
```

**Important**: Your client machine must have Tailscale installed and connected to the same tailnet as the server. Install from https://tailscale.com/download.

---

## Server Setup: Proxmox LXC Step-by-Step

This guide sets up a MagicRepos server on a Proxmox VE LXC container with Tailscale for private networking and SSH key authentication.

### Container Specs

| Parameter | Value | Notes |
|---|---|---|
| **RAM** | **1 GB** | 512 MB works too, 1 GB gives headroom for large pushes |
| **CPU** | **2 cores** | Each push/pull is a short-lived SSH process, 2 cores allow parallel operations |
| **Disk** | **8 GB** (start) | Objects are DEFLATE-compressed (~40-60% of raw size). Scale as needed, Proxmox lets you resize without downtime |
| **OS** | Ubuntu 24.04 | LTS, well-supported |
| **Type** | Unprivileged | More secure, nesting required for Tailscale |

### Step 1: Create the LXC Container

In Proxmox web UI (`https://your-proxmox:8006`):

1. **Download template**: Datacenter > Storage (local) > CT Templates > Templates > search `ubuntu-24.04` > Download
2. **Create CT** with the following settings:

| Setting | Value |
|---|---|
| CT ID | `200` (or any free ID) |
| Hostname | `magic-repos` (or whatever you want) |
| Template | `ubuntu-24.04-standard` |
| Root Disk | `8 GB` |
| CPU | `2 cores` |
| Memory | `1024 MB` |
| Swap | `512 MB` |
| Network | DHCP (doesn't matter, you'll connect via Tailscale) |
| Unprivileged | Yes |
| Nesting | Yes (under Features) |

Or via Proxmox shell:

```bash
pveam download local ubuntu-24.04-standard_24.04-2_amd64.tar.zst

pct create 200 local:vztmpl/ubuntu-24.04-standard_24.04-2_amd64.tar.zst \
  --hostname magic-repos \
  --memory 1024 \
  --swap 512 \
  --cores 2 \
  --rootfs local-lvm:8 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 \
  --features nesting=1 \
  --password <choose-a-root-password>
```

> **Customize**: Change `200` to your preferred CT ID, `magic-repos` to your preferred hostname, and `local-lvm:8` to match your storage.

### Step 2: Enable TUN Device for Tailscale

On the **Proxmox host** shell (not inside the container):

```bash
# Replace 200 with your CT ID
nano /etc/pve/lxc/200.conf
```

Add these two lines at the end:

```
lxc.cgroup2.devices.allow: c 10:200 rwm
lxc.mount.entry: /dev/net/tun dev/net/tun none bind,create=file
```

Start the container:

```bash
pct start 200
```

### Step 3: Base System Setup

Enter the container:

```bash
pct enter 200
```

Update and install essentials:

```bash
apt update && apt upgrade -y
apt install -y openssh-server curl nano
systemctl enable ssh
systemctl start ssh
```

### Step 4: Install Tailscale

```bash
curl -fsSL https://tailscale.com/install.sh | sh
tailscale up --hostname=magic-repos
```

A URL will be printed. Open it in your browser and authorize the machine in your Tailscale admin panel.

Verify:

```bash
tailscale status
# Should show "magic-repos" with a 100.x.x.x IP
```

> **Customize**: Change `magic-repos` to any hostname you want. This is the name you'll use in remote URLs.

### Step 5: Create the Service User

This is the SSH user that handles all MagicRepos connections. Every client connects as this user.

```bash
# Create the user with a home directory
useradd --system --create-home --home-dir /var/lib/magicrepos --shell /bin/bash magicrepos

# Set a password (needed if you ever want to test SSH manually)
passwd magicrepos

# Create the repository storage directory
mkdir -p /var/lib/magicrepos/repositories
chown -R magicrepos:magicrepos /var/lib/magicrepos

# Prepare SSH directory for this user
mkdir -p /var/lib/magicrepos/.ssh
chmod 700 /var/lib/magicrepos/.ssh
touch /var/lib/magicrepos/.ssh/authorized_keys
chmod 600 /var/lib/magicrepos/.ssh/authorized_keys
chown -R magicrepos:magicrepos /var/lib/magicrepos/.ssh
```

> **Customize**: You can name this user anything (e.g. `gituser`, `repos`, `mrserver`). Just replace `magicrepos` everywhere in this guide. This user name becomes the first part of your remote URL: `<this-user>@hostname:...`

### Step 6: Build and Deploy the Server Binary

On your **development machine** (where you have .NET 10 SDK):

```bash
dotnet publish src/MagicRepos.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/server
```

Copy to the container (use the Tailscale IP or hostname):

```bash
scp ./publish/server/MagicRepos.Server root@<TAILSCALE_IP>:/usr/local/bin/magicrepos-server
```

> If your dev machine doesn't have Tailscale, copy via Proxmox host: `pct push 200 ./publish/server/MagicRepos.Server /usr/local/bin/magicrepos-server`

Inside the container, make it executable and test:

```bash
chmod +x /usr/local/bin/magicrepos-server
/usr/local/bin/magicrepos-server --help
# Expected output: "Usage: magicrepos-server <serve --stdin|daemon>"
```

### Step 7: Configure OpenSSH with ForceCommand

```bash
nano /etc/ssh/sshd_config
```

Add `PermitUserEnvironment yes` as a **global** directive (before any `Match` blocks):

```
PermitUserEnvironment yes
```

Then add the `Match` block at the **end** of the file:

```
Match User magicrepos
    ForceCommand /usr/local/bin/magicrepos-server serve --stdin
    PasswordAuthentication no
    AllowTcpForwarding no
    X11Forwarding no
    PermitTTY no
    AllowAgentForwarding no
```

`PermitUserEnvironment yes` is a global directive (cannot be inside a `Match` block) — it allows the `environment=` prefix in `authorized_keys` to be honored. `PasswordAuthentication no` ensures only SSH key auth is accepted (each key identifies a user).

> **Customize**: If you named your user differently in Step 5, replace `magicrepos` in the `Match User` line.

This means: when anyone SSHs as `magicrepos`, instead of a shell, they always get the MagicRepos server protocol handler. Nobody can get a shell through this user.

Restart SSH:

```bash
systemctl restart ssh
```

### Step 8: Generate and Install SSH Key

SSH key auth is required because `magicrepos push/pull` communicates over a binary pipe (stdin/stdout) — there's no way to interactively type a password during the protocol exchange.

**On your client machine**, generate a key if you don't have one:

```bash
# Windows (Git Bash / PowerShell):
ssh-keygen -t ed25519 -N "" -f ~/.ssh/id_ed25519

# Linux / macOS:
ssh-keygen -t ed25519 -N "" -f ~/.ssh/id_ed25519
```

The `-N ""` means no passphrase — the key works automatically without prompting. Tailscale already secures the network.

Copy the public key:

```bash
# Print your public key:
cat ~/.ssh/id_ed25519.pub
# Output looks like: ssh-ed25519 AAAAC3Nza... user@machine
```

**On the container**, add the key with the `environment=` prefix that identifies the user:

```bash
# Use the admin command (recommended):
magicrepos-server add-key <username> /path/to/id_ed25519.pub

# Or manually:
echo 'environment="MAGICREPOS_USER=<username>" ssh-ed25519 AAAAC3Nza... user@machine' >> /var/lib/magicrepos/.ssh/authorized_keys
```

The `environment="MAGICREPOS_USER=<username>"` prefix tells the server who connected. Replace `<username>` with the MagicRepos username for this person (e.g. `magiccactus`, `alice`).

Example `authorized_keys` file:
```
environment="MAGICREPOS_USER=magiccactus" ssh-ed25519 AAAAC3... Micha@DESKTOP
environment="MAGICREPOS_USER=alice" ssh-ed25519 AAAAB5... alice@laptop
```

Or if you have root SSH access to the container:

```bash
# From your client machine:
ssh root@<TAILSCALE_IP> "echo 'environment=\"MAGICREPOS_USER=<username>\" $(cat ~/.ssh/id_ed25519.pub)' >> /var/lib/magicrepos/.ssh/authorized_keys"
```

### Step 9: Verify Everything Works

From your **client machine**:

```bash
# Test SSH connectivity — should connect and immediately close
# (the server starts, gets no valid protocol data, and exits)
ssh magicrepos@<TAILSCALE_HOSTNAME_OR_IP> echo test
```

If you see `Unexpected end of stream` or similar — that's correct. The ForceCommand is working and the server binary runs.

If you see a shell prompt — the ForceCommand isn't applied. Check `/etc/ssh/sshd_config` and run `systemctl restart ssh`.

Now test the actual workflow:

```bash
mkdir test-project && cd test-project
magicrepos init
echo "hello world" > hello.txt
magicrepos add -A
magicrepos commit -m "first commit"
magicrepos remote add origin magicrepos@<TAILSCALE_HOSTNAME_OR_IP>:<your-username>/test-project
magicrepos push
```

If you see `Push completed successfully.` — you're done. The server is running.

Verify on the container:

```bash
ls /var/lib/magicrepos/repositories/<your-username>/test-project.mr/
# Should show: HEAD  objects  refs
```

---

## Personalization

### Changing the Tailscale Hostname

The hostname is set during `tailscale up`:

```bash
# On the container:
tailscale up --hostname=my-custom-name
```

This changes the hostname other machines on your tailnet use to connect. Update your remote URLs accordingly:

```bash
# Old:
magicrepos remote add origin magicrepos@magic-repos:user/repo

# New:
magicrepos remote add origin magicrepos@my-custom-name:user/repo
```

You can also always use the Tailscale IP address directly instead of the hostname (e.g. `100.91.61.66`).

### Changing the SSH Service User

If you want the SSH user to be called something other than `magicrepos` (e.g. `git`, `repos`, `svr`):

1. When creating the user (Step 5), use your preferred name:

```bash
useradd --system --create-home --home-dir /var/lib/magicrepos --shell /bin/bash myuser
```

2. Update the `Match User` block in `/etc/ssh/sshd_config`:

```
Match User myuser
    ForceCommand /usr/local/bin/magicrepos-server serve --stdin
    ...
```

3. Restart SSH: `systemctl restart ssh`

4. Use the new user name in remote URLs:

```bash
magicrepos remote add origin myuser@magic-repos:username/repo
```

### Changing the Data Directory

By default, repositories are stored at `/var/lib/magicrepos/repositories/`. You can change this with the `MAGICREPOS_DATA` environment variable.

Edit the ForceCommand in `/etc/ssh/sshd_config`:

```
Match User magicrepos
    ForceCommand MAGICREPOS_DATA=/srv/repos /usr/local/bin/magicrepos-server serve --stdin
```

Or, to change it permanently for the service user:

```bash
echo 'export MAGICREPOS_DATA=/srv/repos' >> /var/lib/magicrepos/.bashrc
```

Make sure the directory exists and is owned by the service user:

```bash
mkdir -p /srv/repos
chown -R magicrepos:magicrepos /srv/repos
```

### Choosing Your Username

The `<username>` in the remote URL (`magicrepos@host:<username>/repo`) is just a namespace — a folder name on the server. It can be anything you want:

```bash
magicrepos remote add origin magicrepos@magic-repos:magiccactus/MagicRepos
magicrepos remote add origin magicrepos@magic-repos:alice/my-project
magicrepos remote add origin magicrepos@magic-repos:team-alpha/shared-lib
```

This creates repos at:
```
/var/lib/magicrepos/repositories/magiccactus/MagicRepos.mr/
/var/lib/magicrepos/repositories/alice/my-project.mr/
/var/lib/magicrepos/repositories/team-alpha/shared-lib.mr/
```

No registration needed. The server creates the directories automatically on first push.

### Adding More Users / Machines

Every person or machine that needs access needs:

1. **Tailscale** installed and connected to the same tailnet
2. **An SSH key** registered with the server (with their username)

On the new machine:

```bash
ssh-keygen -t ed25519 -N ""
cat ~/.ssh/id_ed25519.pub
# Copy the output (or the file path)
```

On the container (as root), use the admin command:

```bash
magicrepos-server add-key alice /tmp/alice_key.pub
```

Or manually:

```bash
echo 'environment="MAGICREPOS_USER=alice" <paste-the-public-key>' >> /var/lib/magicrepos/.ssh/authorized_keys
```

The username in the `environment=` prefix determines which namespace the user can push to. Alice can push to `alice/*` repos. To grant write access to other repos, see [Access Control](#access-control) below.

---

## Access Control

MagicRepos identifies users by their SSH key. Each key in `authorized_keys` has an `environment="MAGICREPOS_USER=username"` prefix that tells the server who connected.

### Rules

| Operation | Who Can Do It |
|---|---|
| Pull / Clone | Any authenticated user |
| Push to `username/*` | Only if your SSH key maps to `username` |
| Push to someone else's repo | Only if you are listed as a collaborator |
| Create new repo | Only in your own namespace (or as a collaborator) |
| PR create / review | Any authenticated user |
| PR merge | Write access required (owner or collaborator) |

### Managing Collaborators

Use the admin commands on the server to manage collaborators:

```bash
# Grant alice write access to bob/cool-project
magicrepos-server add-collab bob/cool-project alice

# List collaborators
magicrepos-server list-collabs bob/cool-project
# Output:
#   Collaborators for bob/cool-project:
#     alice

# Remove a collaborator
magicrepos-server remove-collab bob/cool-project alice
```

Collaborators are stored in `{MAGICREPOS_DATA}/permissions.json`.

### Admin Commands

| Command | Description |
|---|---|
| `magicrepos-server add-key <user> <key-file>` | Add an SSH key for a user to `authorized_keys` |
| `magicrepos-server add-collab <owner/repo> <user>` | Grant write access to a repository |
| `magicrepos-server remove-collab <owner/repo> <user>` | Revoke write access |
| `magicrepos-server list-collabs <owner/repo>` | List collaborators for a repository |

---

## End-to-End Workflow

### Solo Developer

```bash
# Create a project
mkdir my-app && cd my-app
magicrepos init

# Work on it
echo "# My App" > README.md
echo "console.log('hello')" > index.js
magicrepos add -A
magicrepos commit -m "initial commit"

# Set up remote and push
magicrepos remote add origin magicrepos@magic-repos:myname/my-app
magicrepos push

# Later, on a different machine:
magicrepos clone magicrepos@magic-repos:myname/my-app
cd my-app

# Make changes and push back
echo "more code" >> index.js
magicrepos add -A
magicrepos commit -m "add more code"
magicrepos push

# Back on the original machine:
magicrepos pull
magicrepos log
```

### Team Workflow (Alice & Bob)

```bash
# === Alice's machine ===
mkdir shared-project && cd shared-project
magicrepos init
echo "hello" > file.txt
magicrepos add -A
magicrepos commit -m "initial commit"
magicrepos remote add origin magicrepos@magic-repos:team/shared-project
magicrepos push

# === Bob's machine ===
magicrepos clone magicrepos@magic-repos:team/shared-project
cd shared-project
magicrepos log                    # sees "initial commit"

# Bob works on a feature
magicrepos branch feature
magicrepos checkout feature
echo "new feature" > feature.txt
magicrepos add -A
magicrepos commit -m "add feature"
magicrepos push                   # pushes the feature branch

# === Alice's machine ===
magicrepos pull                   # gets Bob's feature branch
magicrepos checkout feature
magicrepos log                    # sees Bob's commit

# Merge feature into main
magicrepos checkout main
magicrepos branch -l              # see all branches
```

---

## Troubleshooting

### Connection Issues

| Problem | Cause | Fix |
|---|---|---|
| `Connection refused` | SSH not running on container | `systemctl status ssh` on container, then `systemctl start ssh` |
| `Connection timed out` | Tailscale not connected | Run `tailscale status` on both machines. Run `tailscale ping <hostname>` from client |
| `Could not resolve hostname` | Tailscale hostname not found | Use the IP address directly (e.g. `100.91.61.66`). Check `tailscale ip` on the container |
| `tailscale up` hangs | TUN device not configured | Verify `/etc/pve/lxc/<ID>.conf` has the TUN entries, restart the container |

### Authentication Issues

| Problem | Cause | Fix |
|---|---|---|
| `Permission denied (publickey)` | SSH key not in `authorized_keys` | Re-add your public key. Check file permissions (see below) |
| `Permission denied (password)` | Wrong password or password auth disabled | Check `PasswordAuthentication yes` in sshd_config |
| Key auth not working | Wrong file permissions | Run the permission fix commands below |

**Fix SSH permissions** (on container):

```bash
chmod 700 /var/lib/magicrepos/.ssh
chmod 600 /var/lib/magicrepos/.ssh/authorized_keys
chown -R magicrepos:magicrepos /var/lib/magicrepos/.ssh
```

### Push/Pull Issues

| Problem | Cause | Fix |
|---|---|---|
| `Unexpected end of stream while reading from remote` | SSH auth failed silently, or server crashed | Check `ssh magicrepos@host echo test` manually first. Check auth logs (below) |
| `Remote 'origin' not configured` | No remote set up | Run `magicrepos remote add origin magicrepos@host:user/repo` |
| `Push completed` but no data on server | Wrong data directory | Check `ls /var/lib/magicrepos/repositories/` on container |
| Server crashes on connect | Binary incompatible | Rebuild: `dotnet publish ... -r linux-x64 --self-contained true -p:PublishSingleFile=true` |

### Checking Server Logs

On the container:

```bash
# SSH authentication logs
cat /var/log/auth.log | tail -30

# SSH service status
systemctl status ssh

# See live connections as they happen
journalctl -u ssh -f
```

### Server Diagnostics

```bash
# Is the binary working?
/usr/local/bin/magicrepos-server --help

# Does the service user exist?
id magicrepos

# Is ForceCommand configured?
grep -A5 "Match User magicrepos" /etc/ssh/sshd_config

# How many repositories exist?
find /var/lib/magicrepos/repositories -name "*.mr" -type d | wc -l

# Total disk usage
du -sh /var/lib/magicrepos/repositories/

# List all repos
find /var/lib/magicrepos/repositories -name "*.mr" -type d
```

### Common Mistakes

1. **Using `root@` instead of `magicrepos@`** in remote URLs — root doesn't have ForceCommand, you'll get a shell instead of the protocol handler.

2. **Forgetting to restart SSH** after editing `sshd_config` — always run `systemctl restart ssh`.

3. **Not having Tailscale on the client** — the server is only reachable through the Tailscale network. Install Tailscale on every machine that needs access.

4. **Editing authorized_keys as root** — the file ends up owned by root. Always fix: `chown magicrepos:magicrepos /var/lib/magicrepos/.ssh/authorized_keys`.

---

## Internal Design

### Object Model

MagicRepos uses three object types, each identified by its SHA-256 hash:

| Type | Content |
|---|---|
| **Blob** | Raw file bytes |
| **Tree** | Sorted list of entries: `{mode} {name}\0{32-byte hash}` |
| **Commit** | Text format: tree hash, parent hashes, author/committer signatures, blank line, message |

Commit serialization format:

```
tree <64-char hex>\n
parent <64-char hex>\n       (zero or more)
author Name <email> timestamp +offset\n
committer Name <email> timestamp +offset\n
\n
commit message text
```

Tree entry modes (octal):

| Mode | Meaning |
|---|---|
| `100644` | Regular file |
| `100755` | Executable file |
| `40000` | Directory (subtree) |
| `120000` | Symbolic link |

### Object Storage

Every object is stored as a **loose object** on disk:

```
.magicrepos/objects/{first 2 hex chars}/{remaining 62 hex chars}
```

The on-disk format is:

```
DEFLATE( "{type} {content_length}\0{content_bytes}" )
```

The **ObjectId** (SHA-256) is computed over the **uncompressed** bytes including the header. Same content always produces the same hash.

- Hash algorithm: **SHA-256** (32 bytes / 64 hex characters)
- Compression: raw DEFLATE via `System.IO.Compression.DeflateStream`
- Content-addressable: duplicate content is automatically deduplicated

### Index (Staging Area)

The index file at `.magicrepos/index` uses a custom binary format:

```
Header:
  [4B]  Magic: "MRIX" (0x4D 0x52 0x49 0x58)
  [4B]  Version: 1 (uint32 big-endian)
  [4B]  Entry count (uint32 big-endian)

Per entry:
  [8B]  Modification time - seconds since epoch (uint64 BE)
  [4B]  Modification time - nanoseconds (uint32 BE)
  [4B]  File size in bytes (uint32 BE)
  [32B] Object SHA-256 hash
  [2B]  Flags (uint16 BE)
  [var] Path (UTF-8, null-terminated, padded to 8-byte alignment)

Footer:
  [32B] SHA-256 checksum of all preceding bytes
```

### References

References are plain text files under `.magicrepos/refs/`:

```
.magicrepos/
  HEAD                          "ref: refs/heads/main\n" or raw 64-char hex
  refs/
    heads/main                  64-char hex SHA-256 + newline
    heads/feature-x
    tags/v1.0
    remotes/origin/main
```

`HEAD` is a symbolic reference when on a branch, or a detached raw hash.

### Diff Engine

The diff engine implements the **Myers O(ND) shortest edit script** algorithm:

1. Splits input into lines
2. Runs the forward-phase Myers algorithm
3. Backtracks to reconstruct the edit sequence
4. Groups edits into unified diff hunks with 3 lines of context
5. Merges hunks within 6 lines of each other

### Ignore Rules

`.magicignore` files follow `.gitignore` syntax:

- Blank lines and `#` comments are skipped
- `!` prefix negates a rule
- `/` suffix restricts to directories only
- `*` matches any characters except `/`
- `**` matches zero or more path segments
- Leading `/` anchors to the repository root

The `.magicrepos` directory itself is always ignored.

---

## Wire Protocol

Client and server communicate over SSH stdin/stdout using a framed binary protocol.

### Frame Format

```
[4 bytes]  Payload length (uint32 big-endian)
[1 byte]   MessageType enum value
[N bytes]  Payload (encoding depends on message type)
```

### Message Types

| Code | Name | Direction | Payload |
|------|------|-----------|---------|
| 1 | NegotiateRequest | C > S | `"operation\0username\0repoName"` |
| 2 | NegotiateResponse | S > C | `"v1"` |
| 3 | RefAdvertisement | S > C | Newline-separated `"refName hexHash\n"` |
| 4 | RefUpdate | C > S | `"refName\0newIdHex"` per ref |
| 5 | RefWanted | C > S | Newline-separated ref names |
| 6 | PackData | Both | `hexObjectId(64 chars) + compressedObjectBytes` |
| 7 | PackComplete | Both | Empty payload |
| 8 | Ok | S > C | UTF-8 success message |
| 9 | Error | S > C | UTF-8 error message |
| 20-24 | Pr* | Both | Pull request operations |

### Push Flow

```
Client                          Server
  |                                |
  |-- NegotiateRequest(push) ---->|
  |<--- NegotiateResponse(v1) ---|
  |<--- RefAdvertisement --------|
  |                                |
  |--- RefUpdate (per branch) --->|
  |--- PackData (per object) ---->|
  |--- PackComplete ------------->|
  |                                |
  |<--- Ok / Error ---------------|
```

### Pull Flow

```
Client                          Server
  |                                |
  |-- NegotiateRequest(pull) ---->|
  |<--- NegotiateResponse(v1) ---|
  |<--- RefAdvertisement --------|
  |                                |
  |--- RefWanted ---------------->|
  |                                |
  |<--- PackData (per object) ---|
  |<--- PackComplete ------------|
```

---

## Testing

140 tests across 14 test classes:

```bash
dotnet test
```

| Test Class | Count | Covers |
|---|---|---|
| ObjectIdTests | 11 | SHA-256 hashing, hex parsing, equality |
| ObjectTypeTests | 10 | Type string conversion roundtrips |
| BlobObjectTests | 8 | Blob creation, serialization, ID computation |
| TreeObjectTests | 8 | Tree sorting, serialization, mixed modes |
| CommitObjectTests | 8 | Commit fields, serialization format |
| SignatureTests | 9 | Timestamp formatting, offset handling, parsing |
| ObjectSerializerTests | 8 | Compress/decompress roundtrips, ID consistency |
| ObjectStoreTests | 6 | Loose object read/write, directory structure |
| IndexFileTests | 10 | Binary format roundtrip, checksum, entry operations |
| RefStoreTests | 18 | HEAD, branches, symbolic refs, resolution |
| DiffEngineTests | 9 | Myers algorithm, hunk generation, edge cases |
| IgnoreRuleSetTests | 11 | Glob patterns, negation, directory-only rules |
| RepositoryIntegrationTests | 10 | Full workflows: init, stage, commit, branch, checkout |
| MessageSerializerTests | 8 | Protocol framing roundtrips, truncation handling |

---

## Dependencies

| Project | Package | Version | Purpose |
|---|---|---|---|
| Core | *(none)* | | Zero external dependencies |
| Cli | System.CommandLine | 2.0.0-beta5 | Command-line parsing |
| Cli | Spectre.Console | 0.49.1 | Colored terminal output |
| Protocol | MessagePack | 3.1.3 | Binary serialization |
| Server | Microsoft.Extensions.Hosting | 10.0.0-preview.5 | Background service hosting |
| Tests | xUnit | 2.9.3 | Test framework |
| Tests | FluentAssertions | 8.3.0 | Assertion library |
| Tests | coverlet.collector | 6.0.4 | Code coverage |

All projects target **.NET 10.0**.

---

## Roadmap

| Priority | Feature | Status |
|----------|---------|--------|
| 1 | Core VCS: init, add, commit, branch, checkout, reset, diff, status, log | Done |
| 2 | SSH transport: push, pull, clone | Done |
| 3 | Pull request support (server-side) | Done |
| 4 | User identification via SSH keys | Done |
| 5 | Namespace-based access control + collaborators | Done |
| 6 | Pull request CLI commands (create, list, review, merge) | Planned |
| 7 | Three-way merge | Planned |
| 8 | Merge conflict detection and resolution | Planned |
| 9 | Tags (lightweight and annotated) | Planned |
| 10 | Web UI for browsing repos, diffs, and pull requests | Future |
