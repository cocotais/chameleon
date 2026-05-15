from .server import WorkerServer


def main() -> int:
    return WorkerServer().run()


if __name__ == "__main__":
    raise SystemExit(main())

