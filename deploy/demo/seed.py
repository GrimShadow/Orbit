#!/usr/bin/env python3
"""Seeds the demo tenant ("Acme Demo") with a small fictional library through the REAL API of the demo instance (Python standard library only).

  seed.py --api-local http://127.0.0.1:8088 --public http://demo.localhost:8088 [--reseed] [--check-public]

How it talks to the API: --api-local is Caddy on this machine; the Host header is set to the public address, so requests take the same route a
browser's would (Caddy -> API, Caddy -> object storage for presigned uploads) without going through the internet. Sign-in uses a temporary
user and a temporary window in which the web client accepts password grants; both are removed again before this script exits.

Idempotent: everything is looked up before it is created, so a re-run (or a run that stopped half-way) completes the job without duplicates.
All content is fictional (Acme Motors, Roadster, Trail, Sedan, Lumen) and lives here as data, not in the product (ADR-0003).
"""
from __future__ import annotations

import argparse
import datetime as dt
import http.client
import json
import random
import sys
import threading
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import kc  # noqa: E402
import media  # noqa: E402

SEED_VERSION = 1
SEEDER_ID = "0197d000-0000-7000-8000-5eed00000001"
SEEDER_NAME = kc.SEED_PREFIX + "demo"


class ApiError(RuntimeError):
    pass


class Api:
    """Minimal JSON client. Connects to `local`, presents `public` as the Host."""

    def __init__(self, local: str, public: str, token: str):
        u = urllib.parse.urlsplit(local)
        self.host, self.port = u.hostname, u.port or 80
        self.public_host = urllib.parse.urlsplit(public).netloc
        self.token = token
        self._tl = threading.local()

    def _conn(self) -> http.client.HTTPConnection:
        c = getattr(self._tl, "c", None)
        if c is None:
            c = self._tl.c = http.client.HTTPConnection(self.host, self.port, timeout=60)
        return c

    def raw(self, method: str, path: str, body: bytes | None = None, headers: dict | None = None):
        h = {"Host": self.public_host, **(headers or {})}
        for attempt in range(3):
            try:
                c = self._conn()
                c.request(method, path, body=body, headers=h)
                r = c.getresponse()
                return r.status, r.read()
            except (http.client.HTTPException, ConnectionError, TimeoutError):
                self._tl.c = None
                if attempt == 2:
                    raise
                time.sleep(0.5)
        raise AssertionError

    def call(self, method: str, path: str, body=None, ok=(200, 201, 202, 204)):
        data = json.dumps(body).encode() if body is not None else None
        status, raw = self.raw(method, path, data, {"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})
        parsed = json.loads(raw) if raw[:1] in (b"{", b"[") else None
        if status not in ok:
            detail = ""
            if isinstance(parsed, dict):
                detail = parsed.get("detail") or parsed.get("title") or ""
                if parsed.get("errors"):
                    detail += " " + json.dumps(parsed["errors"])
            raise ApiError(f"{method} {path} -> HTTP {status} {detail}".strip())
        return parsed

    get = lambda self, p: self.call("GET", p)  # noqa: E731
    post = lambda self, p, b=None, **k: self.call("POST", p, b if b is not None else {}, **k)  # noqa: E731
    put = lambda self, p, b: self.call("PUT", p, b)  # noqa: E731
    patch = lambda self, p, b: self.call("PATCH", p, b)  # noqa: E731

    def pages(self, path: str):
        sep = "&" if "?" in path else "?"
        cursor = None
        while True:
            page = self.get(f"{path}{sep}limit=100" + (f"&cursor={urllib.parse.quote(cursor)}" if cursor else ""))
            yield from page["items"]
            cursor = page.get("nextCursor")
            if not cursor:
                return


# ---- the content -----------------------------------------------------------------------------------------------------------
RED, ORANGE, BLUE, WHITE, TEAL, SILVER, GREEN, GOLD = ((196, 36, 56), (232, 140, 38), (36, 104, 196), (232, 234, 240), (24, 150, 150),
                                                        (170, 176, 188), (44, 132, 82), (222, 170, 40))

EXTRA_FOLDERS = [("campaigns", "Service camp", "service_camp"), ("campaigns", "Autumn preview", "autumn_preview"), ("brand_assets", "Lumen lighting", "lumen")]

# key, title, folder label path, kind, scene/art params, description, vehicle code, campaign, channels, tags, status, rights key
IMAGES = [
    # -- Roadster
    ("rd-sunset", "Roadster GT on the evening road", "brand_assets.roadster", ("sunset", 11, RED, "roadster"), "Roadster GT heading into the sunset: hero image for the launch.", "acme_roadster_gt_2026", "spring_launch", ["web", "social", "print"], ["roadster", "hero", "sunset"], "published", "inhouse"),
    ("rd-studio", "Roadster GT studio side view", "brand_assets.roadster", ("studio", 12, (214, 214, 222), "roadster"), "Clean studio profile on a dark cyclorama, for brochures and configurators.", "acme_roadster_gt_2026", None, ["web", "print"], ["roadster", "studio"], "published", "inhouse"),
    ("rd-coast", "Roadster Sport on the coast road", "brand_assets.roadster", ("coast", 13, BLUE, "roadster"), "Roadster Sport in Pacific blue, shot along the coast.", "acme_roadster_sport_2026", "spring_launch", ["web", "social"], ["roadster", "coast", "lifestyle"], "published", "inhouse"),
    ("rd-night", "Roadster Sport night drive", "brand_assets.roadster", ("night", 14, GOLD, "roadster"), "City lights and the Lumen headlamp signature.", "acme_roadster_sport_2026", "festive_offer", ["social", "mobile"], ["roadster", "night", "lumen"], "published", "inhouse"),
    ("rd-interior", "Roadster interior at dusk", "brand_assets.roadster", ("interior", 15, WHITE, "roadster"), "Cockpit with the new 10-inch display.", "acme_roadster_gt_2026", None, ["web"], ["roadster", "interior"], "draft", None),
    # -- Trail
    ("tr-mountain", "Trail X above the treeline", "brand_assets.trail", ("mountain", 21, ORANGE, "trail"), "Trail X on a mountain road: the adventure hero shot.", "acme_trail_x_2026", "spring_launch", ["web", "social", "print"], ["trail", "hero", "mountain"], "published", "inhouse"),
    ("tr-river", "Trail XL river crossing", "brand_assets.trail", ("mountain", 22, GREEN, "trail"), "Trail XL in forest green, ready for the weekend.", "acme_trail_xl_2026", None, ["web", "social"], ["trail", "forest", "lifestyle"], "published", "inhouse"),
    ("tr-camp", "Trail X campsite evening", "brand_assets.trail", ("sunset", 23, TEAL, "trail"), "Camp-ready: roof rack and sunset glow.", "acme_trail_x_2025", None, ["social"], ["trail", "camping"], "published", "inhouse"),
    ("tr-studio", "Trail XL studio front quarter", "brand_assets.trail", ("studio", 24, SILVER, "trail"), "Studio shot of the Trail XL for the dealer catalogue.", "acme_trail_xl_2026", None, ["print", "web"], ["trail", "studio"], "draft", None),
    # -- Sedan
    ("sd-city", "Sedan S city night", "brand_assets.sedan", ("night", 31, WHITE, "sedan"), "Sedan S in the city after dark.", "acme_sedan_s_2026", "festive_offer", ["web", "social", "signage"], ["sedan", "city", "night"], "published", "inhouse"),
    ("sd-studio", "Sedan S studio hero", "brand_assets.sedan", ("studio", 32, RED, "sedan"), "Studio hero for the Sedan S campaign.", "acme_sedan_s_2026", None, ["print", "web"], ["sedan", "studio"], "published", "inhouse"),
    ("sd-highway", "Sedan S highway sunrise", "brand_assets.sedan", ("coast", 33, TEAL, "sedan"), "Easy miles in the morning light.", "acme_sedan_s_2026", None, ["web", "mobile"], ["sedan", "highway"], "published", "inhouse"),
    # -- Lumen lighting
    ("lm-headlamp", "Lumen headlamp line art", "brand_assets.lumen", ("diagram", 1, None, None), "The three-module Lumen headlamp array, technical illustration.", None, None, ["web", "print"], ["lumen", "technical", "illustration"], "published", "agency"),
    ("lm-beam", "Lumen adaptive beam diagram", "brand_assets.lumen", ("diagram", 2, None, None), "How the adaptive beam shapes light around other traffic.", None, None, ["web"], ["lumen", "technical", "diagram"], "published", "agency"),
    # -- Campaign banners
    ("sp-hero", "Spring launch hero banner", "campaigns.spring_launch", ("banner", 41, ("SPRING LAUNCH", "ROADSTER GT FROM 24,990", (24, 70, 120), (8, 28, 60)), "roadster"), "Wide hero banner for the spring launch landing page.", "acme_roadster_gt_2026", "spring_launch", ["web", "signage"], ["spring", "banner", "launch"], "published", "inhouse"),
    ("sp-social", "Spring launch social square", "campaigns.spring_launch", ("banner", 42, ("MEET ROADSTER", "BOOK A TEST DRIVE", (170, 40, 70), (60, 14, 40)), "roadster"), "Square-friendly artwork for social feeds.", "acme_roadster_sport_2026", "spring_launch", ["social", "mobile"], ["spring", "social"], "published", "inhouse"),
    ("sp-mobile", "Spring launch mobile story", "campaigns.spring_launch", ("banner", 43, ("TEST DRIVE", "THIS WEEKEND ONLY", (30, 120, 110), (8, 50, 56)), "sedan"), "Story-format teaser.", "acme_sedan_s_2026", "spring_launch", ["mobile", "social"], ["spring", "story"], "draft", None),
    ("fo-hero", "Festive offer hero banner", "campaigns.festive_offer", ("banner", 44, ("FESTIVE OFFER", "ZERO DOWN PAYMENT", (200, 100, 20), (90, 30, 20)), "trail"), "Festive season offer: zero down payment on selected models.", None, "festive_offer", ["web", "print"], ["festive", "offer", "banner"], "published", "inhouse"),
    ("fo-print", "Festive offer showroom poster", "campaigns.festive_offer", ("banner", 45, ("FESTIVE OFFER", "SEE YOUR DEALER", (120, 40, 110), (40, 14, 60)), "sedan"), "A-frame poster artwork for dealer showrooms.", None, "festive_offer", ["print", "signage"], ["festive", "poster", "dealer"], "published", "inhouse"),
    ("fo-email", "Festive offer email header", "campaigns.festive_offer", ("banner", 46, ("HAPPY SEASON", "OFFERS ON ALL MODELS", (30, 90, 160), (14, 40, 90)), "roadster"), "Header for the customer email.", None, "festive_offer", ["web"], ["festive", "email"], "published", "inhouse"),
    ("sc-hero", "Service camp banner", "campaigns.service_camp", ("banner", 47, ("SERVICE CAMP", "FREE 25-POINT CHECK", (50, 110, 70), (16, 50, 40)), "trail"), "Free multi-point check at participating dealers.", None, "service_camp", ["web", "print"], ["service", "camp"], "published", "inhouse"),
    ("sc-poster", "Service camp dealer poster", "campaigns.service_camp", ("banner", 48, ("SERVICE CAMP", "BOOK ONLINE TODAY", (70, 70, 110), (24, 24, 56)), "sedan"), "Printable poster for dealer notice boards.", None, "service_camp", ["print"], ["service", "poster"], "published", "inhouse"),
    # -- Autumn preview (goes through the review workflow, so the Tasks page has content)
    ("au-1", "Autumn preview: Roadster GT teaser", "campaigns.autumn_preview", ("banner", 51, ("AUTUMN PREVIEW", "ROADSTER GT SPECIAL EDITION", (150, 70, 30), (60, 26, 24)), "roadster"), "Teaser for the autumn special edition. Waiting for approval.", "acme_roadster_gt_2026", None, ["web", "social"], ["autumn", "teaser", "special-edition"], "review", None),
    ("au-2", "Autumn preview: Trail X expedition pack", "campaigns.autumn_preview", ("banner", 52, ("EXPEDITION PACK", "TRAIL X AUTUMN PREVIEW", (110, 80, 30), (40, 40, 28)), "trail"), "Expedition accessories pack, draft artwork for review.", "acme_trail_x_2026", None, ["web", "print"], ["autumn", "accessories"], "review", None),
    ("au-3", "Autumn preview: Sedan S comfort edition", "campaigns.autumn_preview", ("banner", 53, ("COMFORT EDITION", "SEDAN S AUTUMN PREVIEW", (90, 60, 100), (34, 24, 52)), "sedan"), "Comfort edition artwork, awaiting brand review.", "acme_sedan_s_2026", None, ["web", "print"], ["autumn", "comfort"], "review", None),
]

MANUAL_CHAPTERS = [
    ("1 Welcome to your Roadster", ["Thank you for choosing an Acme Roadster. This manual is a short guide to the controls and routine care of your vehicle. Keep it in the glove box for quick reference.",
                                    "Acme Motors is a fictional brand used in this demonstration library. The content of this document is sample text."]),
    ("2 Instruments and controls", ["The 10-inch display shows speed, range and driver assistance status. Use the steering wheel switches to move between the drive, media and vehicle screens.",
                                    "Lumen adaptive headlamps switch on automatically in low light. Hold the stalk forward for main beam; the system dips the beam around oncoming traffic."]),
    ("3 Driving and charging", ["Select a drive mode with the dial on the centre console: Comfort, Sport or Eco. Sport sharpens throttle response and steering weight.",
                                "Regenerative braking recovers energy when you lift off. Charging port: press the flap once to release, plug in, and the ring lights show progress."]),
    ("4 Safety", ["Always fasten seat belts before driving. Child seats must be fitted in the rear using the ISOFIX anchor points.",
                  "In an emergency stop, keep the brake pedal pressed firmly. Hazard lights switch on automatically."]),
    ("5 Vehicle identification number (VIN)", ["Every Acme vehicle carries a 17-character VIN. It never contains the letters I, O or Q. Characters 1 to 3 identify the maker, 4 to 8 the model and body, 9 is a check digit, 10 is the model year and 11 the plant.",
                                               "The VIN is stamped on the dashboard plate (visible through the windscreen) and on the driver door pillar sticker. Quote it when you book a service."]),
    ("6 Care and maintenance", ["Service intervals: every 12 months or 15,000 km, whichever comes first. Check tyre pressures monthly, including the spare.",
                                "Use only Acme-approved fluids. Wash with a pH-neutral shampoo and avoid abrasive polishes on the matt finish."]),
]
SERVICE_CHAPTERS = [
    ("1 Before you start", ["Park on level ground, apply the parking brake, and disconnect the 12 V battery. This checklist is sample content for a fictional vehicle."]),
    ("2 25-point check", ["Brakes, tyres, lights, wipers, coolant level, brake fluid, battery health, suspension bushes, drive belts and software status are covered in the standard service camp check.",
                          "Record any findings against the VIN and attach photos in the dealer system."]),
    ("3 Sign-off", ["The service advisor signs the checklist and hands the customer a copy. Next service is due in 12 months or 15,000 km."]),
]

COLLECTIONS_STATIC = [("Spring launch hero set", ["rd-sunset", "tr-mountain", "sp-hero", "rd-coast"]), ("Social ready", ["rd-sunset", "rd-coast", "tr-river", "sp-social", "sd-city"]),
                      ("Dealer showroom pack", ["fo-print", "sc-poster", "sd-studio", "tr-studio"])]
COLLECTION_SMART = ("Everything Lumen", {"query": "Lumen", "filters": {}, "limit": 50})

GLOSSARY = [("Roadster", "locked", None, "Model name: never translated."), ("Lumen", "locked", None, "Lighting technology brand: never translated."),
            ("Acme Motors", "locked", None, "The fictional brand."), ("Test drive", "mapped", {"hi": "टेस्ट ड्राइव", "fr": "essai routier", "de": "Probefahrt"}, "Customer-facing term.")]

USAGE_CHANNELS = ["web", "social", "print", "signage", "mobile", "qr"]


class Seeder:
    def __init__(self, api: Api, log=print):
        self.api, self.log = api, log
        self.folders: dict[str, str] = {}      # label path -> id
        self.assets: dict[str, str] = {}       # key -> asset id
        self.titles: dict[str, dict] = {}      # title -> asset dto

    # -- structure ---------------------------------------------------------------------------------------------------------
    def load_folders(self) -> None:
        self.folders = {f["path"]: f["id"] for f in self.api.get("/api/v1/folders")}

    def ensure_structure(self) -> None:
        self.load_folders()
        for parent, name, label in EXTRA_FOLDERS:
            path = f"{parent}.{label}"
            if path not in self.folders:
                self.api.post("/api/v1/folders", {"parentId": self.folders[parent], "name": name, "label": label})
        self.load_folders()
        # one workflow, assigned to the "Autumn preview" folder; assets submitted there wait for a reviewer
        wf = next((w for w in self.api.get("/api/v1/workflows") if w["name"] == "Brand review"), None)
        if wf is None:
            definition = {"version": 1, "start": "review", "autoPublish": False, "distinctApprovers": False, "steps": [
                {"id": "review", "name": "Brand review", "assignees": {"roles": ["Approver", "Admin"], "users": []}, "mode": "any", "slaHours": 72,
                 "escalation": {"to": {"roles": ["Admin"], "users": []}}, "onApprove": "complete", "onReject": "reject"}]}
            wf = self.api.post("/api/v1/workflows", {"name": "Brand review", "description": "One review step for campaign artwork, due in three days.", "definition": definition})
        folder = next(f for f in self.api.get("/api/v1/folders") if f["path"] == "campaigns.autumn_preview")
        if folder.get("defaultWorkflowId") != wf["id"]:
            self.api.patch(f"/api/v1/folders/{folder['id']}", {"defaultWorkflowId": wf["id"]})

    def ensure_rights(self) -> dict[str, str]:
        today = dt.date.today()
        wanted = {
            "inhouse": dict(name="Acme in-house photography", licenceType="owned", licensor="Acme Motors Marketing", channels=[], regions=[],
                            validFrom=str(today - dt.timedelta(days=400)), validTo=str(today + dt.timedelta(days=1200)), watermarkRequired=False,
                            notes="Shot by the in-house studio for Acme campaigns."),
            "agency": dict(name="Lumen illustration licence", licenceType="licensed", licensor="Northlight Studio (fictional)", channels=["web", "print"], regions=[],
                           validFrom=str(today - dt.timedelta(days=200)), validTo=str(today + dt.timedelta(days=24)), watermarkRequired=False,
                           notes="Illustrations commissioned from an agency. The licence ends soon: renew or replace."),
        }
        have = {r["name"]: r for r in self.api.get("/api/v1/rights")}
        out = {}
        for key, body in wanted.items():
            rec = have.get(body["name"]) or self.api.post("/api/v1/rights", body)
            out[key] = rec["id"]
        return out

    def ensure_vocab_terms(self) -> None:
        pass  # the automotive-sample pack already carries Acme > Roadster/Trail/Sedan > variants > model years

    # -- assets ------------------------------------------------------------------------------------------------------------
    def load_assets(self) -> None:
        self.titles = {a["title"]: a for a in self.api.pages("/api/v1/assets")}

    def render(self, kind: str, seed: int, params, style: str | None) -> tuple[bytes, str, str]:
        if kind == "banner":
            title, sub, a, b = params
            return media.banner(title, sub, a, b, seed, style=style or "roadster").png(), "image/png", "png"
        if kind == "diagram":
            return media.diagram("headlamp" if seed == 1 else "beam", seed).png(), "image/png", "png"
        return media.scene(kind, seed, params, style=style or "roadster").png(), "image/png", "png"

    def make_asset(self, spec) -> dict:
        key, title, folder, art, desc, vehicle, campaign, channels, tags, status, rights = spec
        existing = self.titles.get(title)
        if existing:
            return existing
        meta: dict = {"description": {"en": desc}, "language": ["en"], "channels": channels, "regions": ["global"]}
        if vehicle:
            meta["vehicle"] = [vehicle]
        if campaign:
            meta["campaign"] = [campaign]
        return self.api.post("/api/v1/assets", {"folderId": self.folders[folder], "type": "image", "title": title, "metadata": meta, "tags": tags, "allowIncomplete": True})

    def upload(self, asset_id: str, name: str, data: bytes, content_type: str) -> None:
        up = self.api.post(f"/api/v1/assets/{asset_id}/uploads", {"fileName": name, "size": len(data), "contentType": content_type})
        upload_id = up["upload"]["id"]
        part_size = up["upload"]["partSize"]
        parts = up["parts"]
        for part in parts:
            u = urllib.parse.urlsplit(part["url"])
            n = part["partNumber"]
            chunk = data[(n - 1) * part_size: n * part_size]
            status, _ = self.api.raw("PUT", u.path + "?" + u.query, chunk, {"Content-Type": content_type})
            if status != 200:
                raise ApiError(f"storage refused part {n} of {name}: HTTP {status}")
        self.api.post(f"/api/v1/uploads/{upload_id}/complete", {})
        deadline = time.time() + 150
        while time.time() < deadline:
            s = self.api.get(f"/api/v1/uploads/{upload_id}")
            if s["status"] in ("ingested", "duplicate"):
                return
            if s["status"] in ("failed", "rejected", "quarantined", "infected", "error"):
                raise ApiError(f"upload of {name} ended as {s['status']}: {s.get('error')}")
            time.sleep(0.7)
        raise ApiError(f"upload of {name} did not finish within 150 s (is the worker running?)")

    def create_all(self, rights: dict[str, str]) -> None:
        self.load_assets()
        todo: list[tuple[str, dict, bytes, str, str, str]] = []
        for spec in IMAGES:
            key, title = spec[0], spec[1]
            asset = self.make_asset(spec)
            self.assets[key] = asset["id"]
            if asset.get("currentVersionId") is None:
                art = spec[3]
                data, ctype, ext = self.render(art[0], art[1], art[2], art[3])
                todo.append((key, asset, data, ctype, ext, title))
        # a short video and an audio clip, only if ffmpeg is installed
        extras = self.media_extras()
        for ex in extras:
            asset = self.titles.get(ex["title"]) or self.api.post("/api/v1/assets", {"folderId": self.folders[ex["folder"]], "type": ex["type"], "title": ex["title"], "metadata": ex["metadata"], "tags": ex["tags"], "allowIncomplete": True})
            self.assets[ex["key"]] = asset["id"]
            if asset.get("currentVersionId") is None:
                todo.append((ex["key"], asset, ex["data"], ex["ctype"], ex["ext"], ex["title"]))
        for ex in self.documents():
            asset = self.titles.get(ex["title"]) or self.api.post("/api/v1/assets", {"folderId": self.folders[ex["folder"]], "type": ex["type"], "title": ex["title"], "metadata": ex["metadata"], "tags": ex["tags"], "allowIncomplete": True})
            self.assets[ex["key"]] = asset["id"]
            if asset.get("currentVersionId") is None:
                todo.append((ex["key"], asset, ex["data"], ex["ctype"], ex["ext"], ex["title"]))
        self.log(f"   uploading {len(todo)} files")
        errors: list[str] = []

        def one(item):
            key, asset, data, ctype, ext, title = item
            try:
                self.upload(asset["id"], f"{key}.{ext}", data, ctype)
            except Exception as e:  # noqa: BLE001
                errors.append(f"{title}: {e}")

        with ThreadPoolExecutor(max_workers=4) as pool:
            list(pool.map(one, todo))
        if errors:
            raise ApiError("some uploads failed:\n  " + "\n  ".join(errors))
        # rights and lifecycle
        self.load_assets()
        for spec in IMAGES:
            if spec[10]:
                self.api.post(f"/api/v1/rights/{rights[spec[10]]}/assign", {"assetIds": [self.assets[spec[0]]], "replace": False})
        for ex in extras + self.documents():
            if ex.get("rights"):
                self.api.post(f"/api/v1/rights/{rights[ex['rights']]}/assign", {"assetIds": [self.assets[ex["key"]]], "replace": False})
        self.load_assets()
        for key, status in [(s[0], s[9]) for s in IMAGES] + [(e["key"], e["status"]) for e in extras + self.documents()]:
            self.advance(self.assets[key], status)

    def advance(self, asset_id: str, target: str) -> None:
        a = next(x for x in self.titles.values() if x["id"] == asset_id)
        status = a["status"]
        if target == "draft" or status == target:
            return
        steps = {"review": ["submit"], "published": ["submit", "approve", "publish"]}[target]
        if status == "draft":
            for act in steps:
                self.api.post(f"/api/v1/assets/{asset_id}/transitions/{act}", {})
        elif status == "in_review" and target == "published":
            self.api.post(f"/api/v1/assets/{asset_id}/transitions/approve", {})
            self.api.post(f"/api/v1/assets/{asset_id}/transitions/publish", {})
        elif status == "approved" and target == "published":
            self.api.post(f"/api/v1/assets/{asset_id}/transitions/publish", {})

    def media_extras(self) -> list[dict]:
        out = []
        hero = media.scene("sunset", 11, RED, style="roadster").png()
        mp4 = media.teaser_mp4(hero)
        if mp4:
            out.append(dict(key="vid-teaser", title="Roadster GT launch teaser", folder="brand_assets.roadster", type="video", data=mp4, ctype="video/mp4", ext="mp4",
                            metadata={"description": {"en": "Five-second teaser for the launch, a slow push-in on the hero image."}, "language": ["en"], "channels": ["web", "social"], "regions": ["global"]},
                            tags=["roadster", "teaser", "video"], status="published", rights="inhouse"))
        wav = media.tone_wav()
        mp3 = media.encode_mp3(wav)
        out.append(dict(key="aud-jingle", title="Showroom ambient jingle", folder="campaigns.festive_offer", type="audio", data=mp3 or wav, ctype="audio/mpeg" if mp3 else "audio/wav",
                        ext="mp3" if mp3 else "wav", metadata={"description": {"en": "Eight seconds of soft showroom music for in-store screens."}, "language": ["en"], "channels": ["signage"], "regions": ["global"]},
                        tags=["festive", "audio", "showroom"], status="published", rights="inhouse"))
        return out

    def documents(self) -> list[dict]:
        return [
            dict(key="doc-roadster-manual", title="Roadster owner manual", folder="manuals_and_documents.owner_manuals", type="manual",
                 data=media.pdf("Roadster owner manual", MANUAL_CHAPTERS), ctype="application/pdf", ext="pdf",
                 metadata={"description": {"en": "Owner manual for the Roadster range, with bookmarks for each chapter, including how to read the VIN."}, "language": ["en"],
                           "channels": ["web", "print"], "regions": ["global"], "vehicle": ["acme_roadster_gt_2026"], "manual_type": "owner_manual", "edition": "2026.1"},
                 tags=["roadster", "manual", "vin"], status="published", rights="inhouse"),
            dict(key="doc-trail-service", title="Trail X service camp checklist", folder="manuals_and_documents.service_documents", type="manual",
                 data=media.pdf("Trail X service checklist", SERVICE_CHAPTERS, accent=(0.1, 0.45, 0.3)), ctype="application/pdf", ext="pdf",
                 metadata={"description": {"en": "Checklist used by dealers during the service camp."}, "language": ["en"], "channels": ["print"], "regions": ["global"],
                           "vehicle": ["acme_trail_x_2026"], "manual_type": "service", "edition": "2026.1"},
                 tags=["trail", "service", "checklist"], status="published", rights="inhouse"),
        ]

    # -- collections, glossary, comments, usage ------------------------------------------------------------------------------
    def ensure_collections(self) -> None:
        have = {c["name"] for c in self.api.get("/api/v1/collections")}
        for name, keys in COLLECTIONS_STATIC:
            if name not in have:
                self.api.post("/api/v1/collections", {"name": name, "type": "static", "assetIds": [self.assets[k] for k in keys]})
        name, query = COLLECTION_SMART
        if name not in have:
            self.api.post("/api/v1/collections", {"name": name, "type": "smart", "query": query})

    def ensure_glossary(self) -> None:
        have = {g["term"] for g in self.api.pages("/api/v1/glossary")}
        for term, mode, tr, note in GLOSSARY:
            if term not in have:
                body = {"term": term, "mode": mode, "note": note}
                if tr:
                    body["translations"] = tr
                self.api.post("/api/v1/glossary", body)

    def ensure_comments(self) -> None:
        aid = self.assets.get("sp-hero")
        if not aid or self.api.get(f"/api/v1/assets/{aid}/comments"):
            return
        self.api.post(f"/api/v1/assets/{aid}/comments", {"body": "Love the headline. Can we try a warmer blue behind the car for the print version?"})
        self.api.post(f"/api/v1/assets/{aid}/comments", {"body": "Agreed: I will send a variant for review tomorrow."})

    def usage(self) -> None:
        published = [(k, i) for k, i in self.assets.items() if self.titles_by_id().get(i, {}).get("status") == "published"]
        if not published:
            return
        rnd = random.Random(20261021)
        now = dt.datetime.now(dt.timezone.utc)
        rnd.shuffle(published)
        published = published[:-5]                     # a few assets that were never used, so the "Not used" list has something to show
        weights = [1.0 / (n + 1) ** 0.9 for n in range(len(published))]
        events = []
        for day in range(56, 0, -1):
            level = 0.55 if day > 28 else 1.0 + (28 - day) / 40            # busier recently, so the period comparison shows growth
            if (now - dt.timedelta(days=day)).weekday() >= 5:
                level *= 0.45
            for _ in range(int(rnd.gauss(16, 3) * level)):
                key, aid = rnd.choices(published, weights)[0]
                etype = rnd.choices(["view", "download", "share", "embed", "place", "play"], [52, 16, 10, 8, 10, 4])[0]
                if etype == "play" and not key.startswith(("vid", "aud")):
                    etype = "view"
                at = now - dt.timedelta(days=day, hours=rnd.randrange(7, 20), minutes=rnd.randrange(60))
                events.append({"clientEventId": f"demo-{len(events):05d}", "assetId": aid, "eventType": etype, "channel": rnd.choice(USAGE_CHANNELS),
                               "occurredAt": at.isoformat().replace("+00:00", "Z")})
        for i in range(0, len(events), 400):
            self.api.post("/api/v1/usage/events", {"events": events[i:i + 400]})
        self.log(f"   {len(events)} usage events over the last eight weeks")

    def titles_by_id(self) -> dict[str, dict]:
        return {a["id"]: a for a in self.titles.values()}

    def duplicate_demo(self) -> None:
        """Uploading a picture that is already in the library is recognised as a duplicate, which feeds the 'duplicates prevented' figure."""
        title = "Roadster GT on the evening road (re-upload)"
        if title in self.titles:
            return
        spec = IMAGES[0]
        art = spec[3]
        data, ctype, ext = self.render(art[0], art[1], art[2], art[3])
        a = self.api.post("/api/v1/assets", {"folderId": self.folders["brand_assets.roadster"], "type": "image", "title": title, "allowIncomplete": True, "metadata": {"description": {"en": "Duplicate upload"}}})
        self.upload(a["id"], "dup.png", data, ctype)

    def mark(self) -> None:
        cur = self.api.get("/api/v1/tenants/current")
        settings = {**(cur.get("settings") or {}), "demoSeed": {"version": SEED_VERSION, "at": dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")}}
        self.api.patch("/api/v1/tenants/current", {"settings": settings})

    def run(self) -> None:
        t0 = time.time()
        cur = self.api.get("/api/v1/tenants/current")
        if (cur.get("settings") or {}).get("demoSeed", {}).get("version") == SEED_VERSION:
            self.log("   the demo library is already in place (use --reseed to rebuild it)")
            self.load_assets()
            return
        self.log("   folders, workflow and rights")
        self.ensure_structure()
        rights = self.ensure_rights()
        self.log("   uploading and publishing sample assets")
        self.create_all(rights)
        self.log("   collections, glossary, comments")
        self.ensure_collections()
        self.ensure_glossary()
        self.ensure_comments()
        self.duplicate_demo()
        self.load_assets()
        self.usage()
        self.mark()
        self.log(f"   done in {time.time() - t0:.0f} s: {len(self.assets)} assets")


# ---- checking the public address ---------------------------------------------------------------------------------------------
def check_public(api: Api, public: str, token: str, seeder: Seeder) -> list[str]:
    """Goes the whole way round, through the real public address (and the tunnel, when there is one)."""
    problems: list[str] = []

    def fetch(url: str, headers: dict | None = None, method: str = "GET"):
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Macintosh) orbit-demo-check", **(headers or {})}, method=method)
        for attempt in range(40):               # a brand-new tunnel address needs a few seconds before it resolves everywhere
            try:
                with urllib.request.urlopen(req, timeout=30) as r:
                    return r.status, r.read()
            except urllib.error.HTTPError as e:
                return e.code, e.read()
            except (urllib.error.URLError, TimeoutError, ConnectionError):
                time.sleep(2)
        return 0, b""

    auth = {"Authorization": f"Bearer {token}"}
    code, _ = fetch(f"{public}/api/v1/me", auth)
    print(f"    {'ok  ' if code == 200 else 'FAIL'}  signed-in API call through the public address: {code}")
    if code != 200:
        problems.append("API call with a token")
    first = next((a for a in seeder.titles.values() if a["status"] == "published" and a["type"] == "image"), None)
    if first:
        req = urllib.request.Request(f"{public}/api/v1/assets/{first['id']}/links", data=json.dumps({"renditions": ["thumb", "original"]}).encode(),
                                     headers={**auth, "Content-Type": "application/json", "User-Agent": "Mozilla/5.0 (Macintosh) orbit-demo-check"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                links = json.loads(r.read())["links"]
        except Exception as e:  # noqa: BLE001
            links = {}
            problems.append(f"links: {e}")
        for name, url in links.items():
            code, body = fetch(url)
            ok = code == 200 and len(body) > 100
            print(f"    {'ok  ' if ok else 'FAIL'}  {name} link (signed delivery path) through the public address: {code}, {len(body)} bytes")
            if not ok:
                problems.append(f"{name} link")
            if not url.startswith(public + "/d/"):
                problems.append(f"{name} link does not use the public address: {url.split('/d/')[0]}")
    return problems


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--api-local", required=True)
    ap.add_argument("--public", required=True)
    ap.add_argument("--reseed", action="store_true", help="handled by demo.sh (purges the tenant's content before the API starts); accepted here for symmetry")
    ap.add_argument("--check-public", action="store_true")
    a = ap.parse_args(argv)
    public = a.public.rstrip("/")
    k = kc.Keycloak()
    password = kc.generate_password()
    token = None
    try:
        web = k.client_by_id(kc.CLIENT_ID)
        if web is None:
            raise kc.KcError("the demo realm has no dam-web client: run demo.sh up")
        k.set_direct_grants(web["id"], True)                   # the only window in which the web client accepts a password
        try:
            k.ensure_user(SEEDER_NAME, "Admin", new_password=False, first="Acme", last="Curator", email="curator@acme-demo.example", fixed_id=SEEDER_ID)
            user = k.user_by_name(SEEDER_NAME)
            assert user is not None
            k.set_password(user["id"], password)
            token = k.password_grant(kc.CLIENT_ID, SEEDER_NAME, password)
        finally:
            k.set_direct_grants(web["id"], False)              # closed again right away; the token is all that is needed from here
        api = Api(a.api_local.rstrip("/"), public, token)
        seeder = Seeder(api)
        for attempt in range(30):                              # the API may still be finishing its tenant set-up
            try:
                api.get("/api/v1/me")
                break
            except ApiError:
                time.sleep(1)
        seeder.run()
        if a.check_public:
            problems = check_public(api, public, token, seeder)
            if problems:
                print("public address checks failed: " + "; ".join(problems), file=sys.stderr)
                return 1
        return 0
    except (kc.KcError, ApiError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    finally:
        try:
            k.lock_down()                                      # password grants off, seeder deleted: whatever happened above
        except kc.KcError as e:
            print(f"warning: could not finish the clean-up in Keycloak ({e}); run: python3 deploy/demo/kc.py lockdown", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
