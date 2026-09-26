import type { ProgressModel } from '../../shared/components/JobProgress'

/** Step 2 진행 단계: CNN(이진 판단 ➔ 다중 항목) ➔ VLM 판독 ➔ 교차 검증 */
export const imageProgress: ProgressModel = {
  steps: ['업로드', 'CNN 분석', 'VLM 판독', '교차 검증', '완료'],
  progressOf(job) {
    const message = job.message ?? ''
    switch (job.status) {
      case 'Queued':
        return { step: 0, percent: 6 }
      case 'CnnRunning':
        return { step: 1, percent: message.includes('다중 항목') ? 30 : 18 }
      case 'LlmRunning':
        return { step: 2, percent: 55 }
      case 'Validating':
        return { step: 3, percent: 92 }
      case 'Completed':
        return { step: 4, percent: 100 }
      default:
        return { step: -1, percent: 100 }
    }
  },
}
