// 필드 키 ➔ 화면 표시 이름 (src/Api Modules/Text/Pipeline/DocumentSchemas.cs 와 대응)

export const DOCUMENT_TYPES: Record<string, string> = {
  receipt: '영수증',
  commercial_invoice: '상업송장',
  insurance_claim: '보험 청구서',
  other: '기타',
}

export const FIELD_LABELS: Record<string, string> = {
  // 영수증
  store_name: '상호',
  business_no: '사업자번호',
  date: '거래일',
  time: '거래시각',
  receipt_no: '영수증 번호',
  items: '품목',
  subtotal: '소계(공급가액)',
  tax: '세금',
  gross_total: '할인 전 합계',
  total: '결제 금액',
  payment_method: '결제수단',
  name: '품목명',
  qty: '수량',
  unit_price: '단가',
  amount: '금액',
  // 상업송장
  invoice_no: '송장번호',
  invoice_date: '송장일자',
  seller: '판매자',
  buyer: '구매자',
  currency: '통화',
  description: '품명',
  total_amount: '합계',
  // 보험 청구서
  insured_name: '피보험자',
  resident_no: '주민번호',
  company_name: '회사명',
  department: '부서',
  job_duty: '하는 일',
  address: '주소',
  contact_name: '보상안내 받을 분',
  contact_relationship: '관계',
  contact_phone: '연락처',
  email: '이메일',
  other_insurers: '다른 보험사',
  accident_datetime: '사고일시',
  accident_place: '사고장소',
  diagnosis: '진단명',
  hospital: '치료병원',
  injury_part: '사고경위·아픈부위',
  bank_name: '은행',
  account_no: '계좌번호',
  account_holder: '예금주',
  written_date: '작성일',
  claimant_name: '청구권자',
}

export const label = (key: string) => FIELD_LABELS[key] ?? key

// DB(jsonb)는 키 순서를 보존하지 않으므로 화면에서는 스키마 순서(위 FIELD_LABELS 순서)로 정렬
const ORDER = new Map(Object.keys(FIELD_LABELS).map((key, i) => [key, i]))
export const bySchemaOrder = (a: string, b: string) =>
  (ORDER.get(a) ?? Number.MAX_SAFE_INTEGER) - (ORDER.get(b) ?? Number.MAX_SAFE_INTEGER)

/** 금액 필드는 천 단위 구분 */
export const AMOUNT_KEYS = new Set(['subtotal', 'tax', 'gross_total', 'total', 'unit_price', 'amount', 'total_amount'])
