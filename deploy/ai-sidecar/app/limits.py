"""Global concurrency limiter: a burst queues (bounded wait), then gets 503 busy + Retry-After."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator, Callable
from contextlib import asynccontextmanager
from typing import Any, TypeVar

from starlette.concurrency import run_in_threadpool

from .errors import ApiError

T = TypeVar("T")


class Limiter:
    def __init__(self, max_concurrency: int, queue_timeout_s: float, retry_after_s: int) -> None:
        self._sem = asyncio.Semaphore(max_concurrency)
        self._timeout = queue_timeout_s
        self._retry_after = retry_after_s
        self.max = max_concurrency

    @asynccontextmanager
    async def slot(self) -> AsyncIterator[None]:
        try:
            await asyncio.wait_for(self._sem.acquire(), timeout=self._timeout)
        except TimeoutError as exc:
            raise ApiError(
                503,
                "busy",
                "the service is at capacity; retry shortly",
                headers={"Retry-After": str(self._retry_after)},
            ) from exc
        try:
            yield
        finally:
            self._sem.release()

    async def run(self, fn: Callable[..., T], *args: Any) -> T:
        """Run blocking `fn` in a worker thread while holding a concurrency slot."""
        async with self.slot():
            return await run_in_threadpool(fn, *args)
