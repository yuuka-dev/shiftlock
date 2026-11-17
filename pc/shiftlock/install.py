"""Claude Code の hook 登録（~/.claude/hooks にエントリとパッケージをコピーして settings.json に hook を書く）。"""
from __future__ import annotations

import json
import shutil
from pathlib import Path


MARKER = "shiftlock.py"


HOOK_EVENTS = ("UserPromptSubmit", "Stop")


def hook_command(script: Path, cfg_path: Path) -> str:
    """WSL 版（sh）でも Windows 版（Git Bash）でも動く 1 本のコマンド。
    - /mnt/c がある = WSL → そのまま python3
    - ない = Windows の Git Bash → wsl.exe 経由。MSYS のパス変換（/home → C:/Program Files/Git/home）を止める
    - 本体が見つからへんときは exit 0 で素通り（全プロンプトをブロックせんように）
    """
    inner = f'[ -f "{script}" ] && exec python3 "{script}" hook --config "{cfg_path}" || exit 0'
    return (f'if [ -d /mnt/c ]; then {inner}; '
            f"else export MSYS_NO_PATHCONV=1; exec wsl.exe -e sh -c '{inner}'; fi")


def merge_hooks(settings_path: Path, cmd: str, timeout: int) -> None:
    settings = {}
    if settings_path.exists():
        text = settings_path.read_text(encoding="utf-8-sig")
        settings = json.loads(text) if text.strip() else {}
        bak = settings_path.with_name(settings_path.name + ".bak-shiftlock")
        if not bak.exists():   # 最初の 1 回だけ = shiftlock を入れる前の状態を残す
            bak.write_text(text, encoding="utf-8")
    hooks = settings.setdefault("hooks", {})
    for ev in list(hooks):  # 旧版（PreToolUse 等）も含めて shiftlock を一掃。他の hook はそのまま
        groups = hooks[ev]
        for g in groups:
            g["hooks"] = [h for h in g.get("hooks", []) if MARKER not in h.get("command", "")]
        hooks[ev] = [g for g in groups if g.get("hooks")]
        if not hooks[ev]:
            del hooks[ev]
    for ev in HOOK_EVENTS:
        hooks.setdefault(ev, []).append({"hooks": [{"type": "command", "command": cmd, "timeout": timeout}]})
    settings_path.parent.mkdir(parents=True, exist_ok=True)
    settings_path.write_text(json.dumps(settings, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def install(cfg_path: Path, settings_path: Path, hooks_dir: Path, windows_settings: Path | None = None) -> Path:
    hooks_dir.mkdir(parents=True, exist_ok=True)
    dest = hooks_dir / MARKER
    pkg = Path(__file__).resolve().parent                    # このパッケージ（shiftlock/）
    src = pkg.parent / MARKER                                # エントリ（shiftlock.py）。パッケージと同じフォルダにある
    if not (dest.exists() and dest.samefile(src)):           # hook のフォルダから実行したときは自分を消さへん（綴り違いでも同一判定）
        pkg_dest = hooks_dir / pkg.name
        new, old = hooks_dir / (pkg.name + ".new"), hooks_dir / (pkg.name + ".old")
        for d in (new, old):
            if d.is_dir():
                shutil.rmtree(d)
        shutil.copytree(pkg, new, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
        if pkg_dest.exists():
            pkg_dest.rename(old)                             # 入れ替えは rename で（途中で止まっても本名か .old のどちらかが残る）
        new.rename(pkg_dest)
        if old.is_dir():
            shutil.rmtree(old)                               # 無くなったモジュールが残らんように古いのは捨てる
        shutil.copy2(src, dest)                              # パッケージが揃ってからエントリ（途中で止まっても古い 1 ファイル版のエントリは動く）
    dest.chmod(0o755)
    cmd = hook_command(dest, cfg_path)          # WSL 側のパス（シンボリックリンクは解決しない）
    merge_hooks(settings_path, cmd, 30)
    if windows_settings is not None and windows_settings.resolve() != settings_path.resolve():
        merge_hooks(windows_settings, cmd, 30)  # 別ファイルなら同じコマンドを入れる
    return dest
