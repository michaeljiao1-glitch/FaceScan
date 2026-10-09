import numpy as np

from facescan_app.faces import FaceStore, best_match


def test_store_and_match(tmp_path):
    store = FaceStore(str(tmp_path / "faces.db"))
    store.add("1001", "Facenet512", [1.0, 0.0, 0.0])
    store.add("1002", "Facenet512", [0.0, 1.0, 0.0])
    store.add("1003", "ArcFace", [0.9, 0.1, 0.0])

    enrolled = store.all_for_model("Facenet512")
    assert {a for a, _ in enrolled} == {"1001", "1002"}

    match = best_match(np.array([0.95, 0.05, 0.0]), enrolled, threshold=0.3)
    assert match.account == "1001"

    assert best_match(np.array([0.0, 0.0, 1.0]), enrolled, threshold=0.3) is None

    assert store.remove("1001") == 1
    assert [a for a, _ in store.all_for_model("Facenet512")] == ["1002"]
