# WPF `Run.Text` 기본 TwoWay → 읽기전용 속성 바인딩 시 시작 크래시

## 증상
앱 시작 시 크래시: "TwoWay 또는 OneWayToSource 바인딩은 '...' 형식의 읽기 전용 속성 'XXX'에서 작동하지 않습니다."
해당 XAML 요소가 화면에 실제로 렌더되는 순간 발생(Visibility로 숨겨져 있으면 표시될 때).

## 원인
`Run.Text` 의존 속성은 WPF에서 **기본 바인딩 모드가 TwoWay**다(TextBox.Text 와 동일).
따라서 `<Run Text="{Binding SomeGetOnlyProp}"/>` 처럼 get-only(계산/읽기전용) 속성에 바인딩하면
바인딩이 소스로 write-back을 시도하다 InvalidOperationException 발생.
- 읽기/쓰기 속성은 문제없음 → 그래서 형제 코드가 멀쩡해 보여 놓치기 쉬움.
- `TextBlock.Text`(요소 자체)는 기본 OneWay라 안전. 함정은 `<Run>` 뿐.

## 해결
읽기전용 속성에 `Run.Text`를 바인딩할 땐 반드시 `Mode=OneWay` 명시:
```xml
<Run Text="{Binding DisplayCount, Mode=OneWay}"/>  <!-- 읽기전용(get-only) 속성 예시 -->
```
