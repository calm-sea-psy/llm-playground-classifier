import paddle


def release_cached_memory() -> None:
    """
    Paddle 은 추론 중 잡은 GPU 메모리를 캐시로 계속 들고 있다. 큰 문서 1장에 약 13GB 까지 올라가
    (2026-09-24 측정: 2480×3508 PP-OCR 후 13.0GB ➔ empty_cache 후 2.4GB) 같은 GPU 의 Ollama(gemma4 8.4GB)가
    VRAM 을 못 얻고 공유 메모리로 밀려나므로, 추론이 끝날 때마다 반환한다.
    """
    if paddle.device.is_compiled_with_cuda():
        paddle.device.cuda.empty_cache()
