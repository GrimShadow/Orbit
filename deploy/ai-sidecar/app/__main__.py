"""`python -m app` -> uvicorn with JSON logs and a bounded graceful shutdown."""

from __future__ import annotations

import os

import uvicorn

from .logging_setup import LOG_CONFIG


def main() -> None:
    uvicorn.run(
        "app.main:app",
        host=os.environ.get("AI_HOST", "0.0.0.0"),  # noqa: S104 - container-internal bind
        port=int(os.environ.get("AI_PORT", "8080")),
        log_config=LOG_CONFIG,
        access_log=False,  # the request middleware writes one structured line per request
        timeout_graceful_shutdown=30,
        server_header=False,
    )


if __name__ == "__main__":
    main()
