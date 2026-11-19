import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:purplemagic/main.dart';

import 'preview_test.dart' show FakeCore;

// 验证公告栏存在 CDK 兑换入口按钮（Key('cdk-entry')）。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUp(() => launchArguments = const []);
  tearDown(() => launchArguments = const []);

  testWidgets('announcement bar has CDK entry button', (tester) async {
    tester.view.physicalSize = const Size(1440, 810);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final model = LauncherController(client: FakeCore());
    addTearDown(model.dispose);
    await tester.pumpWidget(LauncherPreview(controller: model));
    await tester.pumpAndSettle();
    final entry = find.byKey(const Key('cdk-entry'));
    expect(entry, findsOneWidget, reason: '公告栏应存在 CDK 兑换入口');
    final text = tester.widget<TextButton>(entry);
    expect(text.onPressed, isNotNull, reason: 'CDK 入口应可点击');
  });
}
