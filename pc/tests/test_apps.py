from shiftlock.config import DEFAULTS, load_config


def test_defaults_have_apps():
    assert DEFAULTS["apps"] == {"registered": {}, "active_input_minutes": 5, "active_cpu_percent": 5}


def test_load_config_merges_apps(tmp_path):
    p = tmp_path / "shifts.json"
    p.write_text('{"apps": {"registered": {"code.exe": "dev"}}}', encoding="utf-8")
    cfg = load_config(p)
    assert cfg["apps"] == {"registered": {"code.exe": "dev"}, "active_input_minutes": 5, "active_cpu_percent": 5}
