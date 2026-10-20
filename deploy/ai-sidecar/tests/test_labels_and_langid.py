from app import langid
from app.registry import load_default_labels


def test_default_labels_are_a_clean_curated_list() -> None:
    labels = load_default_labels()
    assert len(labels) >= 1000
    assert len(labels) == len(set(labels))
    assert all(label == label.strip() and 0 < len(label) <= 100 and not label.startswith("#") for label in labels)


def test_default_labels_contain_no_customer_or_brand_names() -> None:
    blob = " ".join(load_default_labels()).lower()
    for banned in ("mahindra", "tata", "toyota", "ford", "bmw", "samsung", "nike", "coca", "pads4"):
        assert banned not in blob.split(), banned


def test_detect_language_known_languages() -> None:
    cases = {
        "The quick brown fox jumps over the lazy dog and keeps running through the forest.": "en",
        "Der schnelle braune Fuchs springt über den faulen Hund und läuft durch den Wald.": "de",
        "El rápido zorro marrón salta sobre el perro perezoso y sigue corriendo por el bosque.": "es",
        "यह एक परीक्षण वाक्य है जो हिंदी भाषा में लिखा गया है और इसमें कई शब्द हैं।": "hi",
        "இது தமிழ் மொழியில் எழுதப்பட்ட ஒரு சோதனை வாக்கியம் ஆகும்.": "ta",
    }
    for text, expected in cases.items():
        code, confidence = langid.detect(text)
        assert code == expected, (text, code)
        assert 0.5 < confidence <= 1.0


def test_detect_language_is_deterministic_and_handles_noise() -> None:
    text = "Hello world, this is a short sentence."
    assert len({langid.detect(text) for _ in range(5)}) == 1
    assert langid.detect("") == ("und", 0.0)
    assert langid.detect("1234 5678 !!! ???") == ("und", 0.0)
