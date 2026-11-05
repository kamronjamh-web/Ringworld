"""CPU ID persistence regression; run via tools/run_tests.ps1 -Tests audio_studio_ids_regression."""
import concurrent.futures
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from uuid import UUID

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def require(value, message):
    if not value:
        raise AssertionError(message)


def rejects(action, description):
    try:
        action()
    except ValueError:
        return
    raise AssertionError("Did not reject " + description)


def run():
    spec = importlib.util.spec_from_file_location("studio_ids_server", HERE / "server.py")
    server = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = server
    spec.loader.exec_module(server)
    parent = ROOT / "artifacts" / "audio-studio" / "regression"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="generation-ids-", dir=parent) as name:
        scratch = Path(name).resolve()
        require(scratch.parent == parent.resolve(), "Scratch escaped its intended parent")
        server.OUTPUT = scratch / "output"
        server.SEQUENCE = scratch / "generation-sequence.json"
        server.OUTPUT.mkdir()

        def fixture(number, created, generation_id=None):
            job_id = str(UUID(int=number))
            job = {"id": job_id, "created_at": created, "status": "succeeded",
                   "settings": {"prompt": "fixture"}, "provenance": {"untouched": True},
                   "outputs": [{"variation": 1, "wav": "take.wav", "sha256": {"wav": "fixture"}},
                               {"variation": 2, "wav": "take2.wav"}]}
            if generation_id is not None:
                job["generation_id"] = generation_id
            folder = server.OUTPUT / job_id
            folder.mkdir(exist_ok=True)
            (folder / "take.wav").write_bytes(b"immutable audio fixture")
            (folder / "worker-result.json").write_text('{"original":true}', encoding="utf-8")
            server.persist(job)
            return job

        def hash_file(path):
            return hashlib.sha256(path.read_bytes()).hexdigest()

        newer = fixture(3, "2026-10-31T03:00:00Z")
        tie_second = fixture(2, "2026-10-31T01:00:00Z")
        earliest = fixture(1, "2026-10-31T01:00:00Z")
        originals = {job["id"]: json.loads(json.dumps(job)) for job in (newer, tie_second, earliest)}
        immutable = {path: hash_file(path) for path in server.OUTPUT.glob("*/*") if path.name != "metadata.json"}
        server.initialize_history()
        for job, expected in ((earliest, "GEN-0001"), (tie_second, "GEN-0002"), (newer, "GEN-0003")):
            loaded = server.jobs[job["id"]]
            require(loaded["generation_id"] == expected, "Chronological/UUID migration order changed")
            require([take["take_id"] for take in loaded["outputs"]] == [expected + "-01", expected + "-02"], "Migration take IDs incorrect")
            clean = json.loads(json.dumps(loaded))
            del clean["generation_id"]
            for output in clean["outputs"]:
                del output["take_id"]
            require(clean == originals[job["id"]], "Migration changed pre-existing metadata")
        require(all(hash_file(path) == value for path, value in immutable.items()), "Migration changed audio/worker-result bytes")
        before = {path: path.read_bytes() for path in server.OUTPUT.glob("*/metadata.json")}
        # Discard and shuffle in-memory state; restart must use persisted IDs.
        server.jobs = dict(reversed(list(server.jobs.items())))
        server.initialize_history()
        require(all(path.read_bytes() == value for path, value in before.items()), "Restart rewrote existing IDs/metadata")
        server.write_sequence(100)
        server.initialize_history()
        require(server.reserve_generation_id() == "GEN-0101", "High counter was reset")
        # Deleting metadata does not make its already-reserved number available.
        path = server.OUTPUT / earliest["id"] / "metadata.json"
        require(path.resolve().parent.parent == server.OUTPUT.resolve(), "Unsafe fixture path")
        path.unlink()
        server.initialize_history()
        require(server.reserve_generation_id() == "GEN-0102", "Deleted jobs caused ID reuse")
        server.reserve_generation_id()  # Simulate a crash after durable reservation.
        server.initialize_history()
        require(server.reserve_generation_id() == "GEN-0104", "Interrupted reservation was reused")
        fixture(4, "2026-10-31T04:00:00Z", "GEN-9999")
        server.initialize_history()
        require(server.reserve_generation_id() == "GEN-10000", "Assigned maximum or growing padding was lost")
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            allocated = list(pool.map(lambda _: server.reserve_generation_id(), range(40)))
        require(len(set(allocated)) == 40, "Concurrent reservations collided")
        require(sorted(server.generation_number(value) for value in allocated) == list(range(10001, 10041)), "Concurrent sequence skipped/duplicated numbers")
        outputs = [{"variation": 1, "wav": "a.wav"}, {"variation": 2, "wav": "b.wav"}]
        imported = server.outputs_with_take_ids("GEN-10000", outputs)
        require([item["take_id"] for item in imported] == ["GEN-10000-01", "GEN-10000-02"], "Worker-import take IDs incorrect")
        require(all("take_id" not in item for item in outputs), "Worker-import helper mutated raw outputs")
        fixture(5, "2026-10-31T05:00:00Z", "GEN-9999")
        snapshot = server.SEQUENCE.read_bytes()
        rejects(server.initialize_history, "duplicate generation IDs")
        require(server.SEQUENCE.read_bytes() == snapshot, "Validation failure changed the counter")
        duplicate = server.OUTPUT / str(UUID(int=5)) / "metadata.json"
        duplicate.unlink()
        for malformed in ("GEN-001", "GEN-0000", "GEN-00001", "gen-0001", 1, True):
            rejects(lambda: server.generation_number(malformed), "malformed generation ID")
        for invalid in ({"last_reserved": True}, {"last_reserved": -1}, {"last_reserved": 1.5}, {"last_reserved": "2"}, {}, [], None):
            server.SEQUENCE.write_text(json.dumps(invalid), encoding="utf-8")
            rejects(server.initialize_history, "malformed sequence counter")
        require("torch" not in sys.modules and "acestep" not in sys.modules and "diffusers" not in sys.modules,
                "ID regression imported a model runtime")
    print("AUDIO_STUDIO_IDS=PASS", flush=True)


if __name__ == "__main__":
    try:
        run()
    except Exception as exc:
        print(f"AUDIO_STUDIO_IDS=FAIL {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
