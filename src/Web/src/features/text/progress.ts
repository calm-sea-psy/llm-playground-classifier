import type { ProgressModel } from '../../shared/components/JobProgress'

/** Step 1 진행 단계. VLM 폴백·재검증은 같은 단계의 뒷부분으로 표시 */
export const textProgress: ProgressModel = {
  steps: ['업로드', 'OCR', 'LLM 구조화', '검증', '완료'],
  progressOf(job) {
    const message = job.message ?? ''
    switch (job.status) {
      case 'Queued':
        return { step: 0, percent: 6 }
      case 'OcrRunning':
        return { step: 1, percent: 22 }
      case 'LlmRunning':
        if (message.includes('폴백')) return { step: 2, percent: 72 }
        if (message.includes('필드 추출')) return { step: 2, percent: 50 }
        return { step: 2, percent: 38 }
      case 'Validating':
        return { step: 3, percent: message.startsWith('재검증') ? 90 : 62 }
      case 'Completed':
        return { step: 4, percent: 100 }
      default:
        return { step: -1, percent: 100 }
    }
  },
}
