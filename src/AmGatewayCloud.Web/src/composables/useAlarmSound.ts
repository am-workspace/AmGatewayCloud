import { ref, readonly } from 'vue'
import type { AlarmLevel } from '@/types'
import { useAppStore } from '@/stores/app'

/** 音频上下文（惰性创建，需用户交互后才能解锁） */
let audioCtx: AudioContext | null = null

/** 上次播放时间戳，防止高频重复播放 */
let lastPlayTime = 0

/** 最小播放间隔（ms） */
const MIN_INTERVAL = 3000

/** 声音是否已初始化（用户交互解锁） */
const soundInitialized = ref(false)

/** 获取/创建 AudioContext */
function getAudioContext(): AudioContext {
  if (!audioCtx) {
    audioCtx = new AudioContext()
  }
  if (audioCtx.state === 'suspended') {
    audioCtx.resume()
  }
  return audioCtx
}

/** 播放蜂鸣音 */
function playBeep(
  frequency = 880,
  duration = 0.3,
  volume = 0.5,
  repeatCount = 1,
  repeatInterval = 0.5
) {
  const ctx = getAudioContext()
  const now = ctx.currentTime

  for (let i = 0; i < repeatCount; i++) {
    const startTime = now + i * repeatInterval

    // 振荡器
    const oscillator = ctx.createOscillator()
    oscillator.type = 'sine'
    oscillator.frequency.value = frequency

    // 音量
    const gainNode = ctx.createGain()
    gainNode.gain.setValueAtTime(0, startTime)
    gainNode.gain.linearRampToValueAtTime(volume, startTime + 0.02)
    gainNode.gain.linearRampToValueAtTime(0, startTime + duration)

    oscillator.connect(gainNode)
    gainNode.connect(ctx.destination)

    oscillator.start(startTime)
    oscillator.stop(startTime + duration)
  }
}

/** 根据报警级别播放不同音效 */
export function playAlarmSound(level: AlarmLevel) {
  const appStore = useAppStore()

  // 声音开关关闭 → 不播放
  if (!appStore.soundEnabled) return

  // 节流：3 秒内不重复
  const now = Date.now()
  if (now - lastPlayTime < MIN_INTERVAL) return
  lastPlayTime = now

  // 初始化标记
  soundInitialized.value = true

  switch (level) {
    case 'Fatal':
      // Fatal：三声短促高音
      playBeep(1000, 0.2, 0.6, 3, 0.4)
      break
    case 'Critical':
      // Critical：两声中等音
      playBeep(880, 0.25, 0.5, 2, 0.35)
      break
    case 'Warning':
      // Warning：一声低音
      playBeep(660, 0.3, 0.3, 1)
      break
    default:
      // Info 不播放声音
      break
  }
}

/** 用户首次交互时解锁 AudioContext */
export function initSoundOnInteraction() {
  if (soundInitialized.value) return

  const handler = () => {
    getAudioContext()
    soundInitialized.value = true
    document.removeEventListener('click', handler)
    document.removeEventListener('keydown', handler)
  }

  document.addEventListener('click', handler, { once: true })
  document.addEventListener('keydown', handler, { once: true })
}

/** composable: 在组件中使用声音状态 */
export function useAlarmSound() {
  return {
    soundInitialized: readonly(soundInitialized),
    play: playAlarmSound,
    initOnInteraction: initSoundOnInteraction,
  }
}
