#!/usr/bin/env python3
from __future__ import annotations
import hashlib, json, re, shutil, zipfile, os
from pathlib import Path
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / ".package_out"
STAGE = OUT / "SmartPrint_Ad_Production_Kit"
QA_SOURCE = ROOT / ".tmp" / "smartprint-responsive-browser-qa"
MASTER = ROOT / "marketing" / "smartprint-ad" / "MASTER_SCRIPT_v1.0.md"
QA_NAMES = [
    "customer-qr-mobile-390-light.png",
    "shop-workspace-desktop-1440-light.png",
    "landing-desktop-1440-light.png",
    "photo-studio-customer-390x844-dark.png",
    "windows-desktop-minimum-dark.png",
]

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def main() -> None:
    if STAGE.exists():
        shutil.rmtree(STAGE)
    STAGE.mkdir(parents=True)
    source_dir = ROOT / "marketing" / "smartprint-ad"
    for item in source_dir.iterdir():
        target = STAGE / item.name
        if item.is_dir():
            shutil.copytree(item, target)
        elif item.is_file():
            shutil.copy2(item, target)
    people = STAGE / "references" / "people"
    people.mkdir(parents=True, exist_ok=True)
    src = ROOT / "src"
    sources = [
        ("IMG-20261111-WA0013.jpg", "Manav_latest_photo_original.jpg"),
        ("Screenshot_2026-11-08-11-56-39-80_6012fa4d4ddec268fc5c7112bb265e7.jpg", "Manav_profile_photo_original.jpg"),
    ]
    for source_name, target_name in sources:
        source = src / source_name
        if source.is_file():
            shutil.copy2(source, people / target_name)
    for source in sorted(src.glob("file_*")):
        if source.is_file() and source.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}:
            target = people / ("Manav_additional_upload" + source.suffix.lower())
            if not target.exists():
                shutil.copy2(source, target)
    if not (people / "Manav_latest_photo_original.jpg").is_file():
        raise SystemExit("Missing required protagonist source image: src/IMG-20261111-WA0013.jpg")
    (people / "README.md").write_text(
        "# People references\n\n"
        "- Primary identity file: Manav_latest_photo_original.jpg. Crop to the real portrait only; exclude greeting-card art and logo.\n"
        "- Secondary likeness reference: Manav_profile_photo_original.jpg.\n"
        "- Inspect all additional files before treating them as portraits.\n"
        "- Never use a logo or detached Q/QE emblem as a person reference.\n",
        encoding="utf-8",
    )

    qa_dir = STAGE / "references" / "qa"
    qa_dir.mkdir(parents=True, exist_ok=True)
    copied = []
    for name in QA_NAMES:
        source = QA_SOURCE / name
        if source.is_file():
            shutil.copy2(source, qa_dir / name)
            copied.append(name)
    (qa_dir / "README.md").write_text(
        "# QA screenshot references\n\n"
        "These images came from automated responsive browser QA. Some are synthetic fixtures. "
        "They are references only, not proof of a full live transaction. The per-file copies/colour-settings "
        "screen is not included; obtain a fresh synthetic QA capture before presenting that control as real UI. "
        "Never show private information, credentials or live QR codes.\n\n"
        + ("\n".join("- " + name for name in copied) if copied else "No QA screenshots were attached to this run."),
        encoding="utf-8",
    )

    master = MASTER.read_text(encoding="utf-8")
    prompt_dir = STAGE / "clip-prompts"
    prompt_dir.mkdir(parents=True, exist_ok=True)
    pattern = re.compile(
        r"(CLIP\s+(\d{2})\s*\|[^\r\n]+\r?\n[\s\S]*?)"
        r"(?=\r?\nCLIP\s+\d{2}\s*\||\r?\n6\.\s*Hindi master audio script|\Z)",
        re.MULTILINE,
    )
    matches = list(pattern.finditer(master))
    if len(matches) != 20:
        raise SystemExit(f"Expected 20 clip blocks; found {len(matches)}")
    for match in matches:
        block, number = match.group(1).strip(), match.group(2)
        (prompt_dir / f"clip-{int(number):02d}.md").write_text(f"# Clip {number}\n\n{block}\n", encoding="utf-8")

    audio_heading = "6. Hindi master audio script (record separately)"
    audio_start = master.find(audio_heading)
    audio_end = master.find("\n7. Real QA/UI capture plan", audio_start)
    if audio_start >= 0 and audio_end > audio_start:
        (STAGE / "HINDI_AUDIO_SCRIPT.md").write_text(master[audio_start:audio_end].strip() + "\n", encoding="utf-8")

    hashes = []
    for path in sorted(p for p in STAGE.rglob("*") if p.is_file()):
        hashes.append({"path": path.relative_to(STAGE).as_posix(), "size_bytes": path.stat().st_size, "sha256": sha256(path)})
    meta = {
        "package": "SmartPrint_Ad_Production_Kit",
        "source_commit": os.environ.get("GITHUB_SHA", "unknown"),
        "ci_run_id": os.environ.get("CI_RUN_ID", "manual"),
        "built_utc": datetime.now(timezone.utc).isoformat(),
        "clip_count": 20,
        "raw_video_seconds": 200,
        "file_count_before_manifest": len(hashes),
        "files": hashes,
    }
    (STAGE / "PACKAGE_MANIFEST.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    archive_path = OUT / "SmartPrint_Ad_Production_Kit.zip"
    if archive_path.exists():
        archive_path.unlink()
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=8) as archive:
        for path in sorted(p for p in STAGE.rglob("*") if p.is_file()):
            archive.write(path, path.relative_to(OUT).as_posix())
    print(f"Created {archive_path}; 20 clip prompts; {len(list(STAGE.rglob('*')))} package entries.")

if __name__ == "__main__":
    main()
