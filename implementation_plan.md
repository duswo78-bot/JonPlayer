# JonPlayer 성능 극대화 및 아키텍처 개편 (팟플레이어 벤치마킹)

현재 D3DImage 기반의 Push 렌더링 방식에서 탈피하여, **IDXGISwapChain 기반의 Pull 방식 VSync 렌더링**으로 아키텍처를 전면 개편하는 계획입니다. 이 과정에서 가장 큰 과제인 **WPF Airspace 이슈** 해결 방안을 포함합니다.

> [!WARNING]
> 이 변경은 애플리케이션의 핵심 렌더링 파이프라인과 UI 구조를 근본적으로 뒤엎는 대규모 작업입니다. 사전에 코드를 백업해두는 것을 권장합니다.

## User Review Required

> [!IMPORTANT]
> **WPF Airspace (공역) 이슈 해결 방안 승인 요청**
> WPF 윈도우 내부에 Win32 컨트롤(`HwndHost`)을 삽입하면, Win32 영역이 항상 WPF UI(글래스모피즘, 자막, 플레이리스트 등)를 가려버리는 문제가 발생합니다. 이를 해결하기 위해 **투명 오버레이 윈도우(Transparent Overlay Window) 기법**을 제안합니다.
> 
> *   **메인 윈도우:** 비디오 렌더링을 위한 `HwndHost` 전용 창.
> *   **오버레이 윈도우:** 투명(`AllowsTransparency="True"`)하게 설정되어 메인 윈도우 위에 완벽하게 겹쳐지며, 모든 UI 요소(자막, 볼륨, 플레이리스트 등)를 호스팅합니다. 
> *   두 윈도우의 위치와 크기를 동기화하여 사용자에게는 하나의 앱처럼 보이게 합니다.
> 
> 이 방식을 채택해도 괜찮을지 승인 부탁드립니다.

## Open Questions

1. **오디오 렌더러 기준 클럭 (Audio Master Clock)**: 현재 Pull 방식으로 변경 시 비디오 프레임이 VSync에 맞춰 오디오 시간에 동기화되어야 합니다. 오디오 플레이백(NAudio 등을 사용 중인지, WPF MediaElement인지 확인 필요)의 현재 재생 시간을 정확하게 가져올 수 있는 인터페이스가 준비되어 있나요? (현재 FFmpegMediaDecoder 내부에 있다고 가정하고 진행합니다.)
2. **타이머 정밀도 API**: `timeBeginPeriod` 외에 고정밀 대기를 위해 멀티미디어 타이머(`CreateWaitableTimerEx`) 스레드를 별도로 구축할까요? (우선 가장 확실하고 복잡도가 낮은 `timeBeginPeriod(1)` 전역 적용과 렌더링 스레드의 `Present(1, 0)` 블로킹을 조합하는 방식을 추천합니다.)

## Proposed Changes

### Core Synchronization & Timer

#### [MODIFY] [App.xaml.cs](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/App.xaml.cs)
*   `winmm.dll`의 `timeBeginPeriod(1)` 및 `timeEndPeriod(1)` P/Invoke 선언 추가.
*   앱 시작 시 `timeBeginPeriod(1)` 호출하여 전역 타이머 해상도 1ms로 상향, 앱 종료 시 복구.

### Decoder Pipeline (Push -> Pull)

#### [MODIFY] [FFmpegMediaDecoder.cs](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/FFmpegMediaDecoder.cs)
*   **큐(Queue) 도입**: 디코딩된 비디오 프레임을 즉시 렌더러로 쏘는(Push) 대신, 스레드 안전한 큐(`ConcurrentQueue<Frame>`)에 일정량(예: 3~5 프레임) 버퍼링합니다.
*   **A/V Sync 허용 오차 확대**: 오디오 클럭과 비디오 PTS 비교 시 기존의 10ms 타이트한 보정 임계값을 **30ms~50ms**로 확대하여 프레임 드랍/복제를 최소화하고 페이싱을 부드럽게 만듭니다.

### Video Renderer (D3DImage -> HwndHost + SwapChain)

#### [NEW] [VideoHwndHost.cs](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/VideoHwndHost.cs)
*   `System.Windows.Interop.HwndHost`를 상속받는 클래스 생성.
*   `BuildWindowCore`에서 `CreateWindowEx`를 호출하여 렌더링 전용 네이티브 HWND 생성.

#### [MODIFY] [D3D11VideoRenderer.cs](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/D3D11VideoRenderer.cs)
*   기존 `D3DImage`, `D3D9Ex` 상호운용 코드 완전 삭제 (더 이상 필요 없음).
*   생성된 HWND를 기반으로 `IDXGISwapChain` 생성 (DXGI Format).
*   **독립된 렌더 스레드(Render Loop) 추가**:
    *   루프 내에서 `FFmpegMediaDecoder`의 큐에서 현재 오디오 클럭에 가장 잘 맞는 타임스탬프(PTS)의 프레임을 **Pull (가져오기)**.
    *   화면에 그리기 (`UpdateSubresource` -> `Draw`).
    *   **`SwapChain.Present(1, 0)` 호출**: 하드웨어 VSync에 맞춰 스레드가 블로킹되므로 완벽한 프레임 페이싱 보장.

### UI Architecture (Airspace Issue Resolution)

#### [MODIFY] [MainWindow.xaml](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/MainWindow.xaml)
*   비디오를 표시하던 `Image` 컨트롤을 삭제하고, 새로 만든 `VideoHwndHost`를 컨트롤로 삽입.
*   현재 비디오 영역 위에 겹쳐있는 모든 UI 컨트롤(자막, OSD, 플레이리스트, 스플래시 이미지, 글래스모피즘 효과 등)을 분리하여 오버레이로 옮기기 위해 제거/수정.

#### [NEW] [OverlayWindow.xaml](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/OverlayWindow.xaml)
*   `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"` 속성을 가진 투명 윈도우 생성.
*   `MainWindow.xaml`에서 떼어낸 모든 UI 컨트롤(자막, 볼륨, 플레이리스트 등)을 이곳으로 이동.

#### [MODIFY] [MainWindow.xaml.cs](file:///c:/Users/djw7ql/OneDrive%20-%20Aptiv/Antigravity/JonPlayer/JonPlayer/MainWindow.xaml.cs)
*   초기화 시 `OverlayWindow`를 생성하고 `Owner = this`로 설정.
*   `LocationChanged`, `SizeChanged`, `StateChanged` 이벤트를 후킹하여 `OverlayWindow`가 `MainWindow` 위를 한 치의 오차 없이 따라다니도록 동기화 로직 추가.

## Verification Plan

### Manual Verification
1. **Airspace 확인**: 앱을 실행하고 비디오가 재생되는 동안 그 위에 자막이나 플레이리스트 팝업이 투명도와 함께 정상적으로 표시되는지(가려지지 않는지) 확인.
2. **프레임 페이싱 & VSync 확인**: 카메라 패닝 씬이나 부드러운 움직임이 있는 영상을 재생하여 티어링(Tearing)이나 미세한 끊김(Stuttering) 없이 모니터 주사율에 맞춰 완벽하게 출력되는지 육안 확인.
3. **A/V Sync 확인**: 사람의 입모양과 음성을 대조하여 30~50ms 임계값 확대가 실제로 화면의 부드러움을 가져오면서 오디오 싱크는 거슬리지 않게 유지되는지 체감 테스트.

