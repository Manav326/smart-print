# SmartPrint cinematic ad — production kit

This folder is the canonical production source for the Hindi SmartPrint brand film.

## Start here
1. Read VIDEO_AGENT_HANDOFF.md first.
2. Read PRODUCTION_GATES.md and ASSET_MANIFEST.md.
3. Inspect every image in references/people and references/qa.
4. Read MASTER_SCRIPT_v1.0.md and all 20 files in clip-prompts.
5. Use brand/SmartPrint-by-QuantaEdge.svg for the final brand shot.

## Film format
- 20 independent clips, 10 seconds each; 200 seconds of raw generated footage.
- 16:9 landscape; 24 fps preferred; final master 1080p H.264/AAC.
- One main action and one continuous camera take per clip.
- Generate silent/ambience-only visuals. Add all Hindi voice, dialogue, subtitles, music, titles and UI inserts in post.
- Keep 0.5–1.0 second handles for editing.

## Assets
The new portrait from src/IMG-20261111-WA0013.jpg is the primary face reference. It is a greeting graphic, so crop/use only the genuine portrait area and exclude the greeting copy and emblem. The older photo and larger source image are secondary references for identity inspection.

The QA screenshots are genuine repository CI artifacts but are not proof of production state or a full end-to-end customer transaction. See PRODUCTION_GATES.md before using them in the film.

## Video storage
Do not commit large MP4s into ordinary Git history. Store generated clips and the final master in a GitHub Actions artifact or a GitHub Release asset linked from this repository. Never put video-provider API keys in Git.
