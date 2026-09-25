"""CNN 서비스 (2차 Step 2: 흉부 X-ray 소견 분류). 실행: uvicorn main:app --host 127.0.0.1 --port 8002"""

from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Query, UploadFile

from contract import CnnResult
from engines import EngineRegistry, UnknownEngineError
from imaging import ImageDecodeError, decode_gray

registry = EngineRegistry()


@asynccontextmanager
async def lifespan(app: FastAPI):
    for name in registry.preload:
        registry.get(name)
    yield


app = FastAPI(title="CNN Service", lifespan=lifespan)


@app.get("/health")
def health():
    return {
        "status": "ok",
        "default_engine": registry.default_engine,
        "engines": {
            name: {
                "loaded": registry.is_loaded(name),
                "model_version": registry.get(name).model_version if registry.is_loaded(name) else None,
                "labels": registry.get(name).labels if registry.is_loaded(name) else None,
            }
            for name in registry.names
        },
    }


# 동기 함수로 두어 FastAPI 스레드풀에서 실행 (추론이 이벤트 루프를 막지 않도록)
@app.post("/classify", response_model=CnnResult)
def classify(
    file: UploadFile,
    engine: str | None = Query(None, description="생략 시 config.toml 의 default_engine"),
    heatmap: bool = Query(True, description="Grad-CAM 히트맵 포함 여부"),
    target: str | None = Query(None, description="히트맵을 그릴 소견 (생략 시 가장 강한 소견)"),
):
    try:
        cnn = registry.get(engine)
    except UnknownEngineError as e:
        raise HTTPException(400, str(e))
    except FileNotFoundError as e:
        raise HTTPException(503, str(e))
    try:
        image = decode_gray(file.file.read())
    except ImageDecodeError as e:
        raise HTTPException(400, str(e))
    try:
        return cnn.run(image, heatmap=heatmap, target=target)
    except ValueError as e:
        raise HTTPException(400, str(e))
