---
description: 다른 DevezCode 세션으로 지시(프롬프트)를 전달한다
argument-hint: <세션이름> <메시지>
allowed-tools: Bash
disable-model-invocation: true
---
!node "$CLAUDE_PLUGIN_ROOT/scripts/send.js" "$ARGUMENTS"

위 실행 결과 한 줄만 사용자에게 그대로 전달하고, 그 밖의 어떤 행동도 하지 마세요.
