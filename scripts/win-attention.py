#!/usr/bin/env python3
"""Claude Code hook（WSL 专用）：需要用户处理时，在 Windows 显示右下角叠放卡片 + 任务栏彩色角标/进度条底色 + 任务栏闪动。

触发：Stop / Notification(permission_prompt) / PreToolUse(AskUserQuestion|ExitPlanMode) / PostCompact / SubagentStop(/btw)
      PreCompact 只做标记：压缩本身也会触发一次无类型 SubagentStop，靠这个标记略过
原生 Windows（Git Bash）不走本文件：run.sh 直接调 exe --hook，判断逻辑在 win-attention.cs 的 HookMode，改动要两边同步
做法：
  1. hook 找到 Claude 所在的 tty，写一个一次性临时标题（OSC 0），让 Windows 端用 UI Automation 认出是哪个 WT 分页；
  2. 后台进程按需用 Windows 自带 csc.exe 把 win-attention.cs 编译成 exe（按源码哈希命名，源码改了自动重编），
     启动 exe（已有宿主进程时经命名管道交给它，自己立即退出），等宿主认出分页后把标题还原。
测试：echo '{"hook_event_name":"Stop","cwd":"/x/demo"}' | win-attention.py --demo [--wait]
状态档（记录、标记、缓存）放 $CLAUDE_PLUGIN_DATA；不是以 plugin 运行时放 ~/.claude/win-attention/。
非 WSL 环境（macOS、纯 Linux）直接退出，不做任何事。
"""
import base64, fcntl, glob, hashlib, json, os, platform, re, secrets, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "win-attention.cs")
DATA = os.environ.get("CLAUDE_PLUGIN_DATA") or os.path.expanduser("~/.claude/win-attention")
ERR = os.path.join(DATA, "win-attention.err")
LOG = os.path.join(DATA, "win-attention.log")
CACHE = os.path.join(DATA, "cache.json")
CMD = shutil.which("cmd.exe") or "/mnt/c/Windows/System32/cmd.exe"
NET = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
REFS = [NET + "\\WPF\\" + n for n in ("PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll",
                                       "UIAutomationClient.dll", "UIAutomationTypes.dll")] + ["System.Xaml.dll", "System.Drawing.dll", "System.Web.Extensions.dll"]
HINT = "点击跳到该标签页 · 右键关闭"


def compact_flag(session):
    return os.path.join(DATA, "compacting-%s" % re.sub(r"[^\w-]", "", session or "-")[:40])


def last_assistant_text(path):
    try:
        with open(path, encoding="utf-8") as fh:
            lines = fh.readlines()[-300:]
    except OSError:
        return ""
    for line in reversed(lines):
        try:
            rec = json.loads(line)
        except ValueError:
            continue
        if rec.get("type") != "assistant":
            continue
        texts = [c.get("text", "") for c in rec.get("message", {}).get("content", []) or []
                 if isinstance(c, dict) and c.get("type") == "text"]
        if any(t.strip() for t in texts):
            return "\n".join(texts)
    return ""


def pending_tool(path):
    """对话记录里最后一个还没有结果的 tool_use 名称（没有则返回空串）。"""
    try:
        with open(path, encoding="utf-8") as fh:
            lines = fh.readlines()[-200:]
    except OSError:
        return ""
    done = set()
    for line in reversed(lines):
        try:
            content = (json.loads(line).get("message") or {}).get("content")
        except ValueError:
            continue
        for c in reversed(content if isinstance(content, list) else []):
            if not isinstance(c, dict):
                continue
            if c.get("type") == "tool_result":
                done.add(c.get("tool_use_id"))
            elif c.get("type") == "tool_use":
                return "" if c.get("id") in done else c.get("name", "")
    return ""


def known_prompt(path, prompt_id):
    """prompt_id 是否已出现在主对话记录（只看尾部 2MB）。/btw 用全新且不写入记录的 prompt_id；
    提示建议、recap 等其他无类型后台子代理沿用最近一轮的 prompt_id，所以出现过的就不是 /btw。"""
    if not prompt_id:
        return False
    try:
        with open(path, "rb") as fh:
            fh.seek(0, 2)
            fh.seek(max(0, fh.tell() - 2000000))
            return ('"promptId":"%s"' % prompt_id).encode() in fh.read()
    except (OSError, TypeError):
        return False


def ai_title(path):
    """WT 分页标题形如「<转动符号> <会话名>」，用它认分页。会话名优先是 /rename 的自订名
    （Claude Code 预设 terminalTitleFromRename 用它当终端标题），否则是自动标题。
    两个都有就都给（\x1f 分隔，自订名在前），宿主依次尝试。"""
    try:
        with open(path, encoding="utf-8") as fh:
            raw = fh.read()
    except OSError:
        return ""
    out = []
    for kind, field in (("custom-title", "customTitle"), ("ai-title", "aiTitle")):
        i = raw.rfind('"type":"%s"' % kind)
        if i < 0:
            continue
        try:
            line = raw[raw.rfind("\n", 0, i) + 1: raw.find("\n", i) if raw.find("\n", i) >= 0 else None]
            t = (json.loads(line).get(field) or "").strip()
        except ValueError:
            continue
        if t and t not in out:
            out.append(t)
    return "\x1f".join(out)

def find_tty():
    """沿父进程链往上找第一个挂在 /dev/pts/N 上的进程（即 Claude Code 所在的分页）。"""
    pid = os.getppid()
    for _ in range(8):
        for fd in (0, 1, 2):
            try:
                target = os.readlink("/proc/%d/fd/%d" % (pid, fd))
            except OSError:
                continue
            if target.startswith("/dev/pts/"):
                return target
        try:
            with open("/proc/%d/stat" % pid) as fh:
                pid = int(fh.read().rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            break
        if pid <= 1:
            break
    return ""


def set_title(tty, title):
    try:
        fd = os.open(tty, os.O_WRONLY | os.O_NOCTTY)
        try:
            os.write(fd, ("\033]0;%s\007" % title).encode())
        finally:
            os.close(fd)
    except OSError:
        pass


def app_dir():
    """Windows 端 %LOCALAPPDATA%\\claude-attention 的 WSL 路径（首次查询后缓存）。"""
    try:
        with open(CACHE) as fh:
            d = json.load(fh)["app_dir"]
        if os.path.isdir(d):
            return d
    except (OSError, ValueError, KeyError):
        pass
    out = subprocess.run([CMD, "/c", "echo %LOCALAPPDATA%"], capture_output=True, text=True, cwd="/mnt/c").stdout.strip()
    d = subprocess.run(["wslpath", "-u", out], capture_output=True, text=True).stdout.strip() + "/claude-attention"
    os.makedirs(d, exist_ok=True)
    with open(CACHE, "w") as fh:
        json.dump({"app_dir": d}, fh)
    return d


def win(path):
    return subprocess.run(["wslpath", "-w", path], capture_output=True, text=True).stdout.strip()


def ensure_exe(adir):
    with open(SRC, "rb") as fh:
        src = fh.read()
    exe = os.path.join(adir, "claude-attention-%s.exe" % hashlib.sha1(src).hexdigest()[:8])
    if os.path.exists(exe):
        for old in glob.glob(os.path.join(adir, "claude-attention-*.exe")):  # 清理旧版本（仍在运行的删不掉，下次再清）
            if old != exe:
                try:
                    os.remove(old)
                except OSError:
                    pass
        return exe
    with open(os.path.join(DATA, "build.lock"), "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if os.path.exists(exe):
            return exe
        cs = os.path.join(adir, "win-attention.cs")
        shutil.copyfile(SRC, cs)
        csc = subprocess.run(["wslpath", "-u", NET + "\\csc.exe"], capture_output=True, text=True).stdout.strip()
        cmd = [csc, "/nologo", "/target:winexe", "/optimize+", "/codepage:65001", "/out:" + win(exe)] + \
              ["/r:" + r for r in REFS] + [win(cs)]
        r = subprocess.run(cmd, capture_output=True, text=True, cwd=adir)
        if r.returncode != 0 or not os.path.exists(exe):
            sys.stderr.write("csc failed:\n" + r.stdout + r.stderr)
            return ""
        for old in glob.glob(os.path.join(adir, "claude-attention-*.exe")):  # 旧版本（若仍在运行会删不掉，下次再清）
            if old != exe:
                try:
                    os.remove(old)
                except OSError:
                    pass
    return exe


def background(b64, tty, marker):
    adir = app_dir()
    exe = ensure_exe(adir)
    if exe:
        subprocess.Popen([exe, b64], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         cwd="/mnt/c", start_new_session=True)
    if tty and marker:  # 持续重写临时标题直到宿主认出分页（最多 5 秒），再还原
        found = os.path.join(adir, "found-" + marker)
        deadline = time.time() + 5
        while time.time() < deadline and not os.path.exists(found):
            set_title(tty, marker)
            time.sleep(0.08)
        set_title(tty, "")
        try:
            os.remove(found)
        except OSError:
            pass


def clean(text, limit=140):
    text = re.sub(r"[`*#>|]+", "", text or "")
    return re.sub(r"\s+", " ", text).strip()[:limit]


def main():
    if len(sys.argv) >= 5 and sys.argv[1] == "--bg":
        tty, marker = (None if a == "-" else a for a in sys.argv[3:5])
        return background(sys.argv[2], tty, marker)
    if "microsoft" not in platform.release().lower() or not os.path.exists(CMD):  # 只支持 WSL
        return
    os.makedirs(DATA, exist_ok=True)
    try:
        data = json.load(sys.stdin)
    except ValueError:
        data = {}
    event = data.get("hook_event_name", "Stop")
    try:  # 每次触发记一行，便于排查重复弹卡（只记事件类型，不记内容）
        with open(LOG, "a", encoding="utf-8") as lg:
            lg.write("%s %s tool=%s type=%s session=%s argv=%s\n" % (time.strftime("%F %T"), event, data.get("tool_name", "-"),
                     data.get("notification_type", "-"), (data.get("session_id") or "-")[:8], " ".join(sys.argv[1:]) or "-"))
            if event == "SubagentStop":  # 只记字段名，便于区分 /btw 与其他无类型子代理
                lg.write("    keys=%s agent_type=%r\n" % (",".join(sorted(data)), data.get("agent_type")))
    except OSError:
        pass
    flag = compact_flag(data.get("session_id"))
    if event == "PreCompact":  # 压缩开始：立个标记，压缩期间的无类型 SubagentStop 不当作 /btw
        open(flag, "w").close()
        return
    project = os.path.basename((data.get("cwd") or os.getcwd()).rstrip("/")) or "Claude Code"

    if event == "Stop":
        label, color, glyph, progress = "✔ 任务完成", "#34D399", "✔", 2
        summary = data.get("last_assistant_message") or last_assistant_text(data.get("transcript_path", ""))
    elif event == "Notification":
        # AskUserQuestion / ExitPlanMode 弹框几秒后 Claude Code 会再发一次 permission_prompt；PreToolUse 已弹过正确的卡，这里略过
        if pending_tool(data.get("transcript_path", "")) in ("AskUserQuestion", "ExitPlanMode"):
            return
        label, color, glyph, progress = "⚠ 需要你确认", "#F59E0B", "!", 8
        summary = data.get("message", "")
    elif event == "SubagentStop":
        # /btw 旁支提问以无类型子代理执行（agent_type 为空）；Agent 工具派出的子代理有类型，不提示
        if data.get("agent_type"):
            return
        # 提示建议（输入框灰字）、recap 也是无类型子代理，但沿用最近一轮的 prompt_id
        if known_prompt(data.get("transcript_path", ""), data.get("prompt_id")):
            return
        try:  # 压缩进行中（标记 15 分钟内）：这次 SubagentStop 是压缩自己发的
            if time.time() - os.path.getmtime(flag) < 900:
                return
        except OSError:
            pass
        label, color, glyph, progress = "💬 /btw 已回答", "#2DD4BF", "i", 0
        summary = data.get("last_assistant_message", "")
    elif event == "PostCompact":
        try:
            os.remove(flag)
        except OSError:
            pass
        label, color, glyph, progress = "≡ 对话已压缩", "#9CA3AF", "≡", 0
        summary = "自动压缩完成，任务会继续" if data.get("trigger") == "auto" else "手动压缩（/compact）完成"
    elif event == "PreToolUse" and data.get("tool_name") == "ExitPlanMode":
        label, color, glyph, progress = "◆ 计划待你审批", "#A78BFA", "◆", 1
        summary = "计划已写好，等你审阅"
    elif event == "PreToolUse":
        label, color, glyph, progress = "? 需要你选择", "#60A5FA", "?", 1
        qs = (data.get("tool_input") or {}).get("questions") or [{}]
        summary = qs[0].get("question", "")
    else:
        return

    tty = find_tty()
    title = ai_title(data.get("transcript_path", ""))
    # 有对话标题就靠它认分页；没有才用临时标题（会盖住 Claude 的标题，所以两者不同时用）
    marker = "cc-" + secrets.token_hex(5) if (tty and not title and os.environ.get("WT_SESSION")) else ""
    if marker:
        set_title(tty, marker)

    key = data.get("session_id") or os.environ.get("WT_SESSION") or project
    if event == "SubagentStop":
        key += ":btw"  # 不取代同一对话里的其他卡
    fields = {"key": key, "project": clean(project, 80),
              "label": label, "color": color, "summary": clean(summary), "glyph": glyph, "progress": str(progress),
              "hint": HINT, "demo": "1" if "--demo" in sys.argv else "0", "marker": marker, "title": title}
    b64 = base64.b64encode("\n".join("%s\t%s" % kv for kv in fields.items()).encode()).decode()
    cmd = [sys.executable, os.path.abspath(__file__), "--bg", b64, tty or "-", marker or "-"]
    if "--wait" in sys.argv:  # 前台运行，便于排错
        sys.exit(subprocess.run(cmd).returncode)
    with open(ERR, "w") as err:
        subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=err, start_new_session=True)


if __name__ == "__main__":
    main()
