# MASTER HANDOFF PROMPT — SmartPrint cinematic ad

Act as the lead film director, AI-video production supervisor, continuity supervisor, editor and delivery engineer for the SmartPrint by QuantaEdge Hindi advertisement.

Your job is to inspect this complete production package, determine the most reliable and economical way to generate the film, then render the 20 separate 10-second clips in sequence and assemble the final ad. The script is already written. Do not restart creative development or rewrite it without identifying a factual or production blocker.

## 1. Inspect the whole ZIP before doing anything
Read:
- README.md
- PRODUCTION_GATES.md
- ASSET_MANIFEST.md
- PRODUCTION_SNAPSHOT.md
- MASTER_SCRIPT_v1.0.md in full
- every file in clip-prompts, in numerical order from 01 to 20
- every reference image in references/people and references/qa

The primary protagonist portrait is references/people/Manav_latest_photo_original.jpg, copied from src/IMG-20261111-WA0013.jpg. The source image is a greeting graphic; use only the actual portrait area as the identity reference. Do not reproduce its greeting copy, decorations or logo. Inspect the older portrait as a secondary likeness reference. The other uploaded image must be inspected before being treated as a portrait.

## 2. Do a real video-engine and cost audit
A GitHub runner is an execution environment, not a video-generation model. Identify a connected video-generation provider/API that this execution environment can actually call. Verify:
- reference-image or character-reference support for keeping Manav recognisable;
- 10-second generation support or a duration that can safely be trimmed to 10 seconds;
- output quality, aspect ratio, frame rate, watermark, audio and commercial-use terms;
- real current price per clip at the intended quality, including reference-input charges, minimum charges and retries;
- API auth method and whether the repository runner can securely access the provider key.

Do not confuse a model catalogue or pricing page with a callable API. Do not print, log, commit or expose secret keys. Do not create new paid subscriptions or enable billing.

The film needs 20 × 10 = 200 generated seconds before trims. Prepare a per-clip and total cost estimate from current provider pricing. Break out generation, voice, music, storage and editing costs separately. Currency must be stated clearly. Do not guess.

## 3. Credit gate — strict
Do not queue all 20 clips at once.

First:
1. Recommend the least expensive engine that still supports reliable image reference.
2. Show the actual price for a single 10-second pilot and the projected total for 20 clips.
3. Generate exactly one pilot only after the user has approved the cost.
4. Prefer a protagonist identity test (Clip 06) or the arrival shot (Clip 02) to validate face and visual continuity.
5. Let the user inspect and approve the pilot before queueing clips 01–20 in order.

One accepted generation must not be repeated. Before retrying a failed shot, state what failed and what smallest prompt/reference adjustment will fix it. Do not use multiple models to make speculative alternatives. No unapproved bulk runs.

## 4. Locked creative continuity
- Keep the exact same protagonist identity from the real photo in every scene where Manav appears. Preserve facial identity, age, skin tone, hairline, natural proportions and realistic texture. Do not beautify, age-shift or replace the person.
- Wardrobe is locked to the master script: understated dark-navy casual shirt, charcoal trousers and a simple watch. The yellow shirt visible in the portrait is a reference-photo detail, not the film wardrobe.
- Every car shot uses the same white Mahindra XUV500. Never substitute another SUV or change the colour/model. No readable registration number, added decals or fabricated trim.
- The same approachable Indian print-shop owner, late 40s/early 50s, in a light-blue checked shirt, recurs in each shop scene.
- The same second customer, around his 30s, in a beige/olive shirt, recurs in the copy-count mix-up.
- One believable neighbourhood print shop and one tasteful Indian home office recur consistently.
- Natural Indian commercial-film performances. Underplayed, warm and credible; no slapstick or exaggerated conflict.
- Every generated clip is one continuous shot, one primary action, no internal montage and no scene changes. Keep people and important props within the central 80% safe area.
- Attach the same identity and character reference assets on every relevant clip. Do not expect the model to remember previous generations.

## 5. Product and brand truth
- Customer scans the shop Upload QR, chooses files and supported print settings, then sends a request.
- The shop reviews the request and explicitly approves/authorises the print workflow. Upload alone never starts printing.
- Per-file copies and colour/black-and-white are genuine features. If the fresh real UI capture is unavailable, use the abstract document-card graphic specified in Clip 12.
- Do not claim zero mistakes, guaranteed accuracy, instant printing, automatic printing upon upload, universal printer compatibility, or offline printing.
- Do not describe ordinary shop Wi-Fi transfer as end-to-end encrypted.
- Show Private Printing as a product feature only if the shop key is configured, the option is enabled, a successful private upload is verified and the paired shop computer can open the document.
- Google Play Store must not appear as an available download route unless a real public listing is live and verified at final-cut time. Otherwise use the official website/download wording permitted in PRODUCTION_GATES.md.
- Windows download claims must be checked against the current official installer route and current release.

## 6. AI footage rules
- Video model generates people, real-world environments and props only.
- Use blank/neutral/softly defocused phone and computer screens. Never ask the video model to invent SmartPrint UI, QR codes, WhatsApp screens, app-store listing, logos, Hindi text, subtitles or readable controls.
- Use actual current SmartPrint UI screenshots only for short, accurate 1–2 second inserts, never as a screen-by-screen presentation.
- All Hindi dialogue/voice-over, titles, subtitles, app/store marks and logo lockup are added in post.
- Keep the footage silent or ambience-only to avoid incorrect Hindi, inconsistent character voices and lip-sync problems.

## 7. Generate sequentially
Use exact names:
SP_AD_01.mp4 through SP_AD_20.mp4.

Generate in story order. For each clip, preserve:
- the exact prompt and prompt version/hash;
- provider, model and model version;
- duration, resolution, aspect ratio, fps and seed/reference IDs;
- source reference image filenames and SHA-256 hashes;
- generation job ID and final clip SHA-256;
- actual amount charged and any approved retry;
- QC status: pending, accepted or rejected;
- assigned dialogue, sound, title and genuine UI insert in post.

Stop on a continuity failure. Do not conceal a different face, vehicle, wardrobe or shop by pretending the shot passed.

## 8. Edit and deliver
- Assemble clips 01–20 in order; trim the handles and pauses to a target final length around 2:45–3:00.
- Record one consistent Hindi narrator voice plus separate shop-owner and second-customer dialogue. Retain the script's conversational cadence.
- Add natural shop ambience, printer/keyboard/paper sounds and restrained cinematic background music. Keep the dialogue clear on mobile speakers.
- Add Hindi subtitles and concise Hindi supers in post using a readable Devanagari font.
- Use real UI only as brief proof inserts. Never use QA placeholders or synthetic-looking data as proof of real customer activity.
- End with the provided SVG brand lockup on one line: SmartPrint by QuantaEdge. No Q/QE icon, no printer mark, no badge, no white capsule, no second-line signature, no distortion and no AI-generated logo.
- Final line: ग्राहक के लिए आसान… दुकानदार के लिए स्मार्ट।
- Export a 16:9 1080p H.264 MP4 master, 24 fps, AAC stereo, 48 kHz. Retain the separate 10-second clips, audio stems, subtitles, edit project, production ledger and QC report.

## 9. Output paths
Keep production outputs in this repository:
- marketing/smartprint-ad/generated/clips/SP_AD_01.mp4 through SP_AD_20.mp4
- marketing/smartprint-ad/generated/audio/
- marketing/smartprint-ad/generated/subtitles/
- marketing/smartprint-ad/generated/master/SmartPrint_Hindi_Brand_Film.mp4
- marketing/smartprint-ad/generated/production-ledger.json
- marketing/smartprint-ad/generated/QC_REPORT.md

Do not push large MP4s into ordinary Git commits. Upload them as a GitHub Actions artifact named smartprint-ad-final-video, or as a release asset linked to this repository. The user has asked that video files not be sent in ChatGPT.

## 10. Final report
Report the exact generation status, clips accepted/rejected, true cost incurred, final render settings and the GitHub Actions artifact/release URL. Never claim that prompts, stills, a slideshow or a blank render are a completed generated video.
