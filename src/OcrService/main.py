"""OCR 서비스 (Step 1). 실행: uvicorn main:app --host 127.0.0.1 --port 8001"""

import io
from contextlib import asynccontextmanager
from datetime import datetime
from typing import Literal

from fastapi import FastAPI, HTTPException, Query, UploadFile
from fastapi.responses import StreamingResponse

from contract import OcrResult
from engines import EngineRegistry, UnknownEngineError
from imaging import ImageDecodeError, decode_image
from verify_excel import VerifyWorkbook

registry = EngineRegistry()


@asynccontextmanager
async def lifespan(app: FastAPI):
    for name in registry.preload:
        registry.get(name)
    yield


app = FastAPI(title="OCR Service", lifespan=lifespan)


@app.get("/health")
def health():
    return {
        "status": "ok",
        "default_engine": registry.default_engine,
        "engines": {
            name: {
                "loaded": registry.is_loaded(name),
                "model_version": registry.get(name).model_version if registry.is_loaded(name) else None,
            }
            for name in registry.names
        },
    }


STRUCTURE_ENGINE = "ppstructure"


# 문서 파싱 (경로 B): /ocr?engine=ppstructure 와 같음. lines[] + blocks[] + markdown
@app.post("/structure", response_model=OcrResult)
def structure(file: UploadFile):
    return ocr(file, engine=STRUCTURE_ENGINE, export=None)


# 동기 함수로 두어 FastAPI 스레드풀에서 실행 (OCR 추론이 이벤트 루프를 막지 않도록)
@app.post("/ocr", response_model=OcrResult)
def ocr(
    file: UploadFile,
    engine: str | None = Query(None, description="생략 시 config.toml 의 default_engine"),
    export: Literal["xlsx"] | None = Query(None, description="xlsx: 검증용 엑셀로 반환"),
):
    try:
        ocr_engine = registry.get(engine)
    except UnknownEngineError as e:
        raise HTTPException(400, str(e))
    try:
        image = decode_image(file.file.read())
    except ImageDecodeError as e:
        raise HTTPException(400, str(e))

    result = ocr_engine.run(image)
    if export != "xlsx":
        return result

    book = VerifyWorkbook()
    book.add_document(file.filename or "upload", image, result)
    buffer = io.BytesIO()
    book.save(buffer)
    buffer.seek(0)
    filename = f"ocr_verify_{datetime.now():%Y%m%d_%H%M%S}.xlsx"
    return StreamingResponse(
        buffer,
        media_type="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )
