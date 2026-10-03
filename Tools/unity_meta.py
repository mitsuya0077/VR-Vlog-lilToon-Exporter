"""Check Unity asset identities before import can silently omit source files."""
import re


def validate_meta_guids(root):
    seen = {}
    ignored = {".git", "work", "outputs", "artifacts", "Library", "Temp", "Obj", "Build", "Builds", "Logs", "UserSettings", "__pycache__"}
    for path in sorted(root.rglob("*.meta")):
        relative = path.relative_to(root)
        if any(part in ignored for part in relative.parts):
            continue
        values = re.findall(r"^guid:[ \t]*(.*)$", path.read_text(encoding="utf-8-sig"), re.MULTILINE)
        if len(values) != 1 or not re.fullmatch(r"[0-9a-fA-F]{32}", values[0].strip()):
            raise ValueError(f"Invalid Unity GUID in {relative.as_posix()}: expected one 32-digit hexadecimal guid")
        guid = values[0].strip().lower()
        if guid in seen:
            raise ValueError(f"Duplicate Unity GUID: {seen[guid]} and {relative.as_posix()}")
        seen[guid] = relative.as_posix()
