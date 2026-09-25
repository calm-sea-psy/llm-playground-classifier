import type { ProgressModel } from '../../shared/components/JobProgress'

/** Step 3 진행 단계: 영상 분석(CNN·VLM·교차 검증) ➔ 소견서(OCR·요약) ➔ 일치 비교 ➔ 종합 보고서 */
export const multimodalProgress: ProgressModel = {
  steps: ['업로드', '영상 분석', '소견서 요약', '일치 비교', '종합 보고서', '완료'],
  progressOf(job) {
    const message = job.message ?? ''
    switch (job.status) {
      case 'Queued':
        return { step: 0, percent: 5 }
      case 'CnnRunning':
        return { step: 1, percent: message.includes('소견') ? 18 : 12 }
      case 'OcrRunning':
        return { step: 2, percent: 50 }
      case 'LlmRunning':
        if (message.includes('종합 보고서')) return { step: 4, percent: 85 }
        if (message.includes('소견서 요약')) return { step: 2, percent: 58 }
        return { step: 1, percent: 30 }
      case 'Validating':
        return message.startsWith('일치 비교') ? { step: 3, percent: 75 } : { step: 1, percent: 45 }
      case 'Completed':
        return { step: 5, percent: 100 }
      default:
        return { step: -1, percent: 100 }
    }
  },
}
