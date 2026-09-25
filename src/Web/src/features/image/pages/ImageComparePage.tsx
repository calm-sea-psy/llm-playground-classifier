import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { MultiDropzone } from '../../../shared/components/MultiDropzone'
import { SystemSpecs } from '../../../shared/components/SystemSpecs'
import {
  applyImageCombo,
  createImageExperiment,
  deleteImageExperiment,
  getImageExperiment,
  getImageSettings,
  getImageSettingsOptions,
  listImageExperiments,
  sameImageSettings,
  type ImageExperimentDetail,
  type ImageExperimentSummary,
  type ImageSettings,
  type ImageSettingsDto,
  type ImageSettingsOptions,
} from '../api'
import { Disclaimer } from '../components/Disclaimer'
import { ImageComboEditor } from '../components/ImageComboEditor'

const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp'
const MAX_FILES = 50
const MAX_COMBOS = 4
/** 건당 대략적인 처리 시간 (2차-2 측정: CNN 수십 ms + VLM 4~9초) */
const SEC_PER_JOB = 7

/**
 * X-ray 모델 비교: 영상 N장 × 판독 조합 M개 실험 ➔ 저장 ➔ 조합별 점수·순위 ➔ 1위 추천,
 * [기본으로 적용]을 누르면 판독 기본 설정이 바뀐다 (Text 모델 비교와 같은 흐름)
 */
export function ImageComparePage() {
  const navigate = useNavigate()
  const [options, setOptions] = useState<ImageSettingsOptions | null>(null)
  const [current, setCurrent] = useState<ImageSettingsDto | null>(null)
  const [experiments, setExperiments] = useState<ImageExperimentSummary[]>([])
  const [latest, setLatest] = useState<ImageExperimentDetail | null>(null)
  const [error, setError] = useState<string | null>(null)

  const [name, setName] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [combos, setCombos] = useState<ImageSettings[]>([])
  const [submitting, setSubmitting] = useState(false)

  const reload = useCallback(async () => {
    const [s, list] = await Promise.all([getImageSettings(), listImageExperiments()])
    setCurrent(s)
    setExperiments(list)
    const done = list.find((e) => e.status === 'Completed' && e.recommended)
    setLatest(done ? await getImageExperiment(done.id) : null)
    return s
  }, [])

  useEffect(() => {
    getImageSettingsOptions()
      .then(async (o) => {
        const s = await reload()
        setOptions(o)
        // 기본 조합 2개: 현재 기본(독립 판독) + VLM 에 CNN 결과를 보여 주는 조합
        setCombos([s.settings, { ...s.settings, vlmSeesCnn: !s.settings.vlmSeesCnn }])
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [reload])

  const running = experiments.some((e) => e.status === 'Running')
  useEffect(() => {
    if (!running) return
    const timer = setInterval(() => reload().catch(() => {}), 5000)
    return () => clearInterval(timer)
  }, [running, reload])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSubmitting(true)
    setError(null)
    try {
      const created = await createImageExperiment(name.trim(), files, combos)
      navigate(`/image/experiments/${created.id}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setSubmitting(false)
    }
  }

  const apply = async (experimentId: string, index: number) => setCurrent(await applyImageCombo(experimentId, index))

  const remove = async (id: string) => {
    if (!confirm('이 실험 기록을 삭제할까요? (처리된 작업은 판독 기록에 남습니다)')) return
    try {
      await deleteImageExperiment(id)
      await reload()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  const recommended = latest?.recommendedIndex != null ? latest.combos[latest.recommendedIndex] : null
  const jobs = files.length * combos.length

  return (
    <>
      <Disclaimer />
      <SystemSpecs />

      <section className="card defaults">
        <div className="defaults-row">
          <div>
            <span className="spec-label">판독 기본 설정</span>
            <strong>{current?.summary ?? '…'}</strong>
            {current?.note && <span className="muted small">근거: {current.note}</span>}
          </div>
          {recommended && latest && (
            <div className="recommend">
              <span className="spec-label">
                추천 · 최근 실험 <Link to={`/image/experiments/${latest.id}`}>{latest.name}</Link> 1위 (점수{' '}
                {recommended.score?.toFixed(3)})
              </span>
              <strong>{recommended.summary}</strong>
              {current && sameImageSettings(current.settings, recommended.settings) ? (
                <span className="chip chip-ok">이미 기본 설정</span>
              ) : (
                <button className="primary small-button" onClick={() => apply(latest.id, recommended.index)}>
                  기본으로 적용
                </button>
              )}
            </div>
          )}
        </div>
      </section>

      <form className="card upload-card" onSubmit={submit}>
        <div>
          <h2>새 실험</h2>
          <p className="muted small">
            같은 X-ray 들을 조합별로 판독해 CNN·VLM 의 폐렴 판단 정확도, 두 판단의 일치율, 처리 시간을 비교합니다. 정답은 파일
            이름으로 찾습니다 (Kaggle 소아: person… = 폐렴 · IM-… = 정상, IU 성인: 소견서). 정답이 있는 영상이어야 순위를 매깁니다.
          </p>
        </div>
        <label className="small name-field">
          실험 이름
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="예: IU 성인 30장 VLM 독립 vs CNN 참고" maxLength={200} />
        </label>
        <MultiDropzone accept={ACCEPT} files={files} onFiles={setFiles} disabled={submitting} max={MAX_FILES} />
        {options && (
          <div className="combos">
            {combos.map((c, i) => (
              <ImageComboEditor
                key={i}
                index={i}
                value={c}
                options={options}
                isDefault={!!current && sameImageSettings(current.settings, c)}
                disabled={submitting}
                onChange={(v) => setCombos(combos.map((x, j) => (j === i ? v : x)))}
                onRemove={combos.length > 1 ? () => setCombos(combos.filter((_, j) => j !== i)) : undefined}
              />
            ))}
            {combos.length < MAX_COMBOS && (
              <button type="button" className="add-combo" onClick={() => setCombos([...combos, combos[combos.length - 1]])}>
                + 조합 추가
              </button>
            )}
          </div>
        )}
        <div className="upload-actions">
          <span className="muted small">
            {jobs ? `작업 ${jobs}건 · 예상 약 ${Math.max(1, Math.round((jobs * SEC_PER_JOB) / 60))}분` : 'X-ray 를 선택하세요'}
          </span>
          <button type="submit" className="primary" disabled={!files.length || !combos.length || submitting}>
            {submitting ? '업로드 중…' : '실험 시작'}
          </button>
        </div>
        {error && <p className="text-bad small">{error}</p>}
      </form>

      <section className="card">
        <h3>실험 기록</h3>
        {experiments.length === 0 ? (
          <p className="muted small">아직 실험이 없습니다.</p>
        ) : (
          <div className="table-scroll">
            <table className="jobs">
              <thead>
                <tr>
                  <th>이름</th>
                  <th>규모</th>
                  <th>상태</th>
                  <th className="hide-sm">추천 조합 (1위)</th>
                  <th className="hide-sm">시각</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {experiments.map((e) => (
                  <tr key={e.id}>
                    <td>
                      <Link to={`/image/experiments/${e.id}`}>{e.name}</Link>
                    </td>
                    <td className="small tabular">
                      {e.docCount}장 × {e.comboCount}조합{e.labeled && <span className="chip">정답 있음</span>}
                    </td>
                    <td className="small">
                      {e.status === 'Completed' ? (
                        <span className="badge badge-ok">완료</span>
                      ) : (
                        <span className="badge badge-run">
                          {e.done}/{e.total}
                        </span>
                      )}
                    </td>
                    <td className="small hide-sm">
                      {e.recommended ? `${e.recommended} · ${e.bestScore?.toFixed(3)}` : <span className="muted">—</span>}
                    </td>
                    <td className="muted small tabular hide-sm">{new Date(e.createdAt).toLocaleString('ko-KR')}</td>
                    <td>
                      {e.status === 'Completed' && (
                        <button className="link-button small" onClick={() => remove(e.id)}>
                          삭제
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  )
}
