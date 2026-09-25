/** CNN 소견 영문 라벨 ➔ 한국어 (API FindingNames 와 같은 표) */
const FINDINGS: Record<string, string> = {
  Atelectasis: '무기폐',
  Consolidation: '경화',
  Infiltration: '침윤',
  Pneumothorax: '기흉',
  Edema: '폐부종',
  Emphysema: '폐기종',
  Fibrosis: '섬유화',
  Effusion: '흉수',
  Pneumonia: '폐렴',
  Pleural_Thickening: '흉막 비후',
  Cardiomegaly: '심장비대',
  Nodule: '결절',
  Mass: '종괴',
  Hernia: '탈장',
  'Lung Lesion': '폐 병변',
  Fracture: '골절',
  'Lung Opacity': '폐 음영',
  'Enlarged Cardiomediastinum': '종격동 확대',
}

export const findingName = (label: string) => FINDINGS[label] ?? label

export const POPULATION_NAMES = { adult: '성인', pediatric: '소아' } as const

/** "pneumonia:Pneumonia" ➔ "소아 폐렴 모델 (Pneumonia)" 식 설명 */
export function sourceName(source: string) {
  const [engine, label] = source.split(':')
  const model = engine === 'pneumonia' ? '소아 폐렴 모델' : engine === 'xrv' ? 'xrv 소견 모델' : engine
  return `${model} · ${findingName(label)}`
}

/** 기준값 바로 위(+0.02 미만) 양성 = 경계(판단 보류). API CnnFinding.Borderline 과 같은 규칙 (xrv 소견만, 기준 0.9 이상 제외) */
export const isBorderline = (f: { probability: number; threshold: number; positive: boolean }) =>
  f.positive && f.threshold < 0.9 && f.probability < f.threshold + 0.02
