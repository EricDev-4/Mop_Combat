# SplashPark Arena

## 실행

`Assets/Scenes/SplashPark_Arena.unity`를 열고 Play → 기존 닉네임/접속 UI를 사용합니다.
Build Settings의 첫 번째 씬으로 등록했습니다. 기존 SampleScene은 두 번째 씬으로 유지됩니다.

## 맵 구성

- 약 54 × 60m, 2~6인용 소규모 전투를 염두에 둔 워터파크 맵입니다. 인원 제한이나 팀 규칙을 새로 추가하지는 않았습니다.
- 중앙 분수 광장과 두 개의 측면 경로. 좌우 보행로 높이는 2.5m이며 양쪽 끝에 경사로가 연결됩니다.
- 파랑/주황 출입구에 각 3개씩 총 6개 스폰 지점. 현재 RoomManager는 모든 지점에서 무작위로 선택하며 색상은 위치 구분입니다.
- 엄폐물, 펌프하우스, 탈의실, 워터슬라이드, 감시탑, 야자수, 정원, 상자 등 제공된 FBX 모델 163개 인스턴스를 배치했습니다.
- 실제 모델 메시를 이용한 정적 충돌체와 맵 외곽 경계를 구성했습니다. 보행로는 작은 장식 턱에 걸리지 않도록 평탄한 데크·난간·지지대 충돌체를 별도로 사용합니다.
- 분수·수로·워터슬라이드는 환경 소품입니다. 수영·슬라이딩 등의 별도 게임 기능은 추가하지 않았습니다.
- 제공된 경사로와 보행로 모델로 필요한 형태를 구성할 수 있어 ProBuilder는 사용하지 않았습니다.

## 씬 구조와 연결

`SplashPark_Arena` 아래 Ground / Fountain / Blue Entrance / Orange Entrance / Flanks / Cover / Gardens / Perimeter / SpawnPoints / Wayfinding / Preview Cameras 그룹으로 정리했습니다.

RoomManager, EventSystem 및 기존 접속 UI는 유지하고 기존 테스트 환경은 새 씬의 `Legacy_SampleScene_Disabled`에 비활성 상태로 보관했습니다. 원본 SampleScene 파일과 원본 FBX는 덮어쓰지 않았습니다. 기존 씬의 저장되지 않은 내용도 새 씬을 Save As 할 때 함께 보존했습니다.

RoomManager에는 직렬화된 `roomName`을 추가했습니다. 이 씬의 방 이름은 `splash-park`, 기존 기본값은 `test`입니다. 스폰 시 각 지점의 회전을 사용하도록 변경했습니다.

## 검증

- Unity 컴파일 및 Play Mode 콘솔 오류 없음.
- 6개 스폰의 바닥·플레이어 캡슐 여유 공간 확인.
- 1m 격자와 캡슐 스윕으로 모든 스폰의 지상 경로 연결 확인 (연결된 셀 2,172개).
- 두 경사로 경로를 0.2m 간격으로 검사: 연속된 바닥, 작은 높이 변화, 2m 캡슐 머리 공간 확인.
- 반대쪽 스폰 간 9개 사선이 엄폐물/분수에 의해 차단됨을 확인.
- PUN 오프라인 방에서 실제 Player 프리팹 스폰, 본인 소유권, 바닥 정착, RoomCam 종료 및 FP 카메라 활성화 확인.
- 실제 다중 클라이언트 전투, 프레임 성능, 이동 키를 이용한 전체 맵 주행은 별도 플레이테스트가 필요합니다.

이미지와 검사 결과: `Logs/SplashPark/Overview_Camera.png`, `PlayMode.png`, `validation.txt`, `runtime.txt`.
