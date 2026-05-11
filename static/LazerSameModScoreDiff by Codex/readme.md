# LazerSameModScoreDiff

현재 플레이 점수와 같은 모드/같은 배속의 osu!lazer 최고 리플레이 점수 차이를 표시하는 tosu 플러그인입니다.

## 필요 앱

- tosu: 기본 `127.0.0.1:24050`
- LazerReplayCompare: 기본 `127.0.0.1:24052`

기존 `24051` replay timeline 서버는 사용하지 않습니다. 리플레이 목록과 timeline 모두 `LazerReplayCompare`에서 가져옵니다.

## 동작 방식

1. tosu에서 현재 점수, 판정 수, 모드, 비트맵 파일 경로를 받습니다.
2. `LazerReplayCompare /replays`에서 현재 곡의 리플레이 목록을 받습니다.
3. 현재 모드와 배속이 같은 리플레이만 남깁니다.
4. 그중 최고 점수 리플레이의 `/timeline`을 불러옵니다.
5. 현재 누적 판정 수와 같은 노트 인덱스의 리플레이 점수를 비교합니다.

표시는 현재 기록이 더 높으면 `+점수차`, 같으면 `0`, 낮으면 `-점수차`입니다.
