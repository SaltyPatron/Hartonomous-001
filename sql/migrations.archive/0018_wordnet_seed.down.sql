-- 0018_wordnet_seed.down.sql
DELETE FROM substrate.edge_type WHERE code IN (
    'antonym', 'hypernym', 'instance_hypernym', 'hyponym', 'instance_hyponym',
    'member_holonym', 'substance_holonym', 'part_holonym',
    'member_meronym', 'substance_meronym', 'part_meronym',
    'attribute', 'derivationally_related',
    'domain_of_synset_topic', 'member_of_domain_topic',
    'domain_of_synset_region', 'member_of_domain_region',
    'domain_of_synset_usage', 'member_of_domain_usage',
    'entailment', 'cause', 'also_see', 'verb_group',
    'similar_to', 'participle_of_verb', 'pertainym',
    'in_synset', 'has_word', 'has_verb_frame',
    'irregular_morphology', 'has_verb_example'
);

DELETE FROM substrate.semantic_relation_type WHERE code IN (
    'antonym', 'hypernym', 'instance_hypernym', 'hyponym', 'instance_hyponym',
    'member_holonym', 'substance_holonym', 'part_holonym',
    'member_meronym', 'substance_meronym', 'part_meronym',
    'attribute', 'derivationally_related',
    'domain_of_synset_topic', 'member_of_domain_topic',
    'domain_of_synset_region', 'member_of_domain_region',
    'domain_of_synset_usage', 'member_of_domain_usage',
    'entailment', 'cause', 'also_see', 'verb_group',
    'similar_to', 'participle_of_verb', 'pertainym'
);

DELETE FROM substrate.lexname;
