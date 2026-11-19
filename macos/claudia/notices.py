"""任务通知的去重和节流（照搬 Windows 版 app/src/TaskNoticePolicy.cs）"""


class TaskNoticePolicy:
    def __init__(self, started_at):
        self.started = started_at
        self.seen = set()
        self.shown = {}

    def accept(self, nid, kind, ts, now, enabled, foreground, background_only):
        if not nid or nid in self.seen:
            return False
        self.seen.add(nid)
        if len(self.seen) > 2000:
            self.seen = {nid}
        if kind not in ("complete", "error", "waiting"):
            return False
        if ts != ts or ts < self.started or ts > now + 5 or now - ts > 60:
            return False
        if not enabled or background_only and foreground:
            return False
        # 一小段时间里来好几条（重复的信号）只弹一次，也不攒着补弹
        if kind in self.shown and now - self.shown[kind] < 8:
            return False
        self.shown[kind] = now
        return True
